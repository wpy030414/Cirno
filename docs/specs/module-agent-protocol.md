# Spec — Agent 后端协议适配（Claude Code / Codex）

## 要构建什么

`Cirno.Agents.Claude` 与 `Cirno.Agents.Codex` 两个适配器：各自把一种 Agent 后端的 stdio 协议翻译成
`Cirno.Agents.Abstractions` 定义的同一套归一化事件，并把界面上的决策（审批、中断、发送、换模型）
编码回对应后端的格式。

判定标准：界面层不应出现任何后端专有字段名；新增第三种后端只需新增一个适配器。

## 行为

### 归一化事件

| 事件 | 含义 |
|---|---|
| `SessionInit` | 会话就绪：后端会话 id、实际生效模型、可用工具、权限模式、能力位 |
| `TurnStarted` / `TurnCompleted` | 一轮工作的起止；完成含状态与用量 |
| `ItemStarted` / `ItemDelta` / `ItemCompleted` | 条目生命周期；`completed` 为权威态 |
| `ApprovalRequested` / `ApprovalResolved` | 审批请求与终态 |
| `AgentFault` | 可归一化的错误（含可重试标记） |

条目类型：`UserMessage` / `AssistantMessage` / `Reasoning` / `Plan` / `CommandExecution` /
`FileChange` / `ToolCall` / `Approval` / `Notice` / `Error` / `Unknown`。
每个条目都保留原始载荷，供调试视图使用。

### Claude Code 适配

- 一个会话对应一个进程，stdin 常开即多轮；进程结束即会话结束。
- 启动时由 Cirno 生成会话 UUID 并显式指定，使本地会话记录与后端会话一一对应。
- 增量流与全量消息会重复表达同一内容：以增量驱动打字效果，收到全量后做幂等覆盖，不追加。
- 工具调用与工具结果按 `tool_use_id` 配对；命令类工具映射为 `CommandExecution`，编辑类工具映射为 `FileChange` 并自行生成 diff。
- 审批：后端发 `control_request`（子类型 `can_use_tool`）→ 界面决策 → 回 `control_response`（`allow` / `deny`，可带改写的输入或「总是允许」规则）。
- **每个审批请求必须有终态应答**，否则后端工具调用永久阻塞且无超时；会话销毁时统一回 `deny`。
- 中断通过控制通道发送；切换模型/权限模式优先走控制通道，不可用时退化为重启进程 + 恢复会话。

### Codex 适配

- 一个 `codex app-server` 进程承载多个 thread；连接必须先握手（初始化请求 → 响应 → 初始化完成通知）。
- 会话：`thread/start`、`thread/resume`、`thread/fork`、`thread/read`、`thread/list`、`thread/archive`。
- 轮次：`turn/start`、`turn/steer`、`turn/interrupt`，另有 `thread/rollback` 与压缩。
- 通知映射：`item/*` 生命周期与增量、`turn/diff/updated`（整轮 diff）、`turn/plan/updated`（计划卡）、`thread/tokenUsage/updated`（用量）。
- 审批：服务端发 `item/commandExecution/requestApproval` / `item/fileChange/requestApproval`，
  回 `accept` / `acceptForSession` / `decline` / `cancel` / 带策略修正的接受；
  有网络审批上下文时必须换文案（目标主机），不能当作命令预览展示。
- 模型清单来自 `model/list`；会话级模型通过 `turn/start` 传递。

## 输入 / 输出

**输入**

- 用户输入：纯文本 + 附件（图片按后端支持的形式编码）。
- 会话配置：工作目录、provider、模型、权限模式、沙箱策略（仅 Codex）。
- 用户决策：审批结果、中断请求、发送追加输入。

**输出**

- 归一化事件流（见上表）。
- 失败时的结构化错误：后端错误码 + 可读文案 + 是否可重试。
- 后端版本信息（写入会话元数据，「关于」页可查）。

## 约束

- 只用 stdio，不依赖网络端口、不依赖后端私有 HTTP 接口。
- 解析必须宽容：未知字段忽略，未知条目降级为 `Unknown`。
- 子进程由 Job Object 托管，应用退出时整棵进程树必须被清理。
- 适配器不得直接触碰 UI、数据库与设置；只通过接口回调。
- 子进程的标准错误要持续读取并保留环形缓冲，避免管道阻塞导致假死。

## 边界条件

- 后端未安装 / 版本过低 → 会话创建时明确报错，给出安装或升级指引。
- 后端进程异常退出 → 会话进入 `Failed`，保留已有条目，允许重试。
- 会话进行中进程崩溃 → 以恢复方式重建会话，历史从本地库补齐。
- 审批请求到达时会话已被删除 → 立即回绝，避免阻塞。
- 后端在流式过程中改变模型或压缩上下文 → 记为 `Notice`，不打断渲染。
- 用量/配额超限、上下文超限 → 归一化为可识别的错误类别，界面给可操作建议。

## 验收标准

- [ ] 用真实抓包夹具回放，产出的事件序列与快照一致。
- [ ] Claude：完成一次含 Bash + Edit 工具调用的会话，审批可选允许/拒绝，`Esc` 可中断。
- [ ] Codex：完成一次含命令与文件改动的轮次，diff 与计划卡正确渲染。
- [ ] 同一窗口内两种后端会话并存，互不串流。
- [ ] 手动杀掉后端进程，界面在 2 秒内进入失败态且不卡死。
- [ ] 每个审批请求都有终态应答（可用日志断言）。
- [ ] 退出应用后无残留 `claude` / `codex` 进程。

## 完成定义

两个适配器通过各自夹具测试，并在真实后端上跑通「新建 → 多轮工具调用 → 审批 → 中断 → 恢复」全链路，
且归一化事件模型未因某个后端而新增特例字段。
