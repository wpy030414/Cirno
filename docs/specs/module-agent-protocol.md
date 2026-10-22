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

**已实测的启动契约**（CLI 2.1.301）：

- 一个会话对应一个进程，stdin 常开即多轮；进程结束即会话结束。
- 必需参数：`--print --input-format stream-json --output-format stream-json --verbose --include-partial-messages`。
- **审批必须显式打开**：`--permission-prompt-tool stdio`（隐藏参数）配合 `--permission-prompts host`。缺任一项时，需要授权的工具会被直接拒绝，工具结果返回 `"...you haven't granted it yet"`。
- **宿主必须先发 `initialize` 控制请求**：`{"type":"control_request","request_id":"…","request":{"subtype":"initialize","hooks":{}}}`。不发握手时 CLI 同样不会把 `can_use_tool` 路由给宿主（已实测对比：握手前工具被拒，握手后被路由）。
- 会话 id 由 Cirno 生成 UUID 经 `--session-id` 指定，使本地记录与后端会话一一对应；恢复用 `--resume <id>`。

**审批报文形状**（实测）

请求：

```json
{"type":"control_request","request_id":"…","request":{
  "subtype":"can_use_tool","tool_name":"Bash","display_name":"Bash",
  "input": { "command": "…" }, "description":"…",
  "decision_reason_type":"rule","decision_reason_rule_source":"flagSettings",
  "tool_use_id":"call_…"}}
```

应答（允许，`updatedInput` 原样回传，不得改写工具参数）：
`{"type":"control_response","response":{"subtype":"success","request_id":"…","response":{"behavior":"allow","updatedInput":{…}}}}`

应答（拒绝）：`{"behavior":"deny","message":"…"}`，工具结果会带 `is_error:true` 与这段文案。

**控制通道其余能力**（实测）

- 中断：`{"subtype":"interrupt"}` → 应答 `{"still_queued":[]}`，随后出现一条 `[Request interrupted by user]` 用户消息与 `stop_reason:null` 的 result。
- 切换权限模式：`{"subtype":"set_permission_mode","mode":"acceptEdits"}` → 应答 `{"mode":…}`。

**解析要点**

- 增量流与全量消息会重复表达同一内容：以增量驱动打字效果，收到全量后按 `blk:<message_id>:<index>` 幂等覆盖，不追加。
- 部分第三方网关（实测环境）下 `stream_event` 的 `event` 不带 `type` 字段，因此**增量只能当增强**，权威内容一律取自 `assistant` 全量消息。
- 工具调用与工具结果按 `tool_use_id` 配对；`Bash` → `CommandExecution`，`Edit/Write/MultiEdit` → `FileChange` 并自行生成 diff，其余 → `ToolCall`。
- 被中断的轮次 `stop_reason` 为 `null` 且 `is_error` 为 `true`，必须映射为 `Interrupted`，不能显示成失败。
- **每个审批请求必须有终态应答**，会话销毁时统一回 `deny`。

### Codex 适配

- 一个 `codex app-server` 进程承载多个 thread；连接必须先握手（初始化请求 → 响应 → 初始化完成通知）。
- 会话：`thread/start`、`thread/resume`、`thread/fork`、`thread/read`、`thread/list`、`thread/archive`。
- 轮次：`turn/start`、`turn/steer`、`turn/interrupt`，另有 `thread/rollback` 与压缩。
- 通知映射：`item/*` 生命周期与增量、`turn/diff/updated`（整轮 diff）、`turn/plan/updated`（计划卡）、`thread/tokenUsage/updated`（用量）。
- 审批：服务端发 `item/commandExecution/requestApproval` / `item/fileChange/requestApproval`，
  回 `accept` / `acceptForSession` / `decline` / `cancel` / 带策略修正的接受；
  有网络审批上下文时必须换文案（目标主机），不能当作命令预览展示。
- 模型清单来自 `model/list`；会话级模型通过 `turn/start` 传递。

## 协议回归护栏

协议是最容易悄悄坏掉的一层：CLI 改一个字段名，界面就少一条消息，而没有任何编译错误。
因此协议形状必须由夹具承载（见 ADR-124、ADR-125）。

### 分层

| 层 | 测什么 | 依赖 |
|---|---|---|
| 夹具回放（7 例） | 一行 stream-json → 归一化事件序列 | 无（纯函数） |
| 端到端（4 例） | 进程启动 → 管道读写 → 初始化握手 → 审批往返 → 中断 | 假 CLI（`tests/Cirno.FakeClaude`） |

### 夹具清单

`tests/Cirno.Agents.Claude.Tests/fixtures/`：

| 夹具 | 覆盖 |
|---|---|
| `text-turn.jsonl` | 初始化握手 → 文本增量 → result |
| `tool-call.jsonl` | 流式增量合并、tool_use / tool_result 补全 |
| `approval-allow.jsonl` | 审批请求 → 允许应答 |
| `approval-deny.jsonl` | 审批请求 → 拒绝（标记为错误） |
| `file-change.jsonl` | diff 渲染；成功回执不混入 diff |
| `interrupted.jsonl` | `stop_reason: null` 表示中断，不是失败 |

### 断言要点

- 审批的 `updatedInput` 必须**原样**回传，不得改写工具参数。
- 同一个 `request_id` 不可重复应答。
- 工具结果的成功回执（「已更新」）不进入 `FileChange` 的 diff 正文。
- `stop_reason: null` 且 `is_error: true` 表示**中断**（用户按了 Esc），不是失败。
- 中断不应产生 `interrupt_no_ack` 之类的伪错误。

### 端到端假 CLI

`tests/Cirno.FakeClaude` 按 `CIRNO_FAKE_CLAUDE_SCRIPT` 给定的剧本逐行回放预录 stream-json，
并自动应答宿主的 `control_request`（initialize / interrupt）。测试用 `.cmd` 包装它，
通过 `CIRNO_CLAUDE_PATH` 注入被测进程。

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

- [x] 用真实抓包夹具回放，产出的事件序列与快照一致（7 个夹具用例全绿）。
- [x] Claude：完成一次含工具调用的会话，审批可允许（实测真机点击）、可中断（实测 `--stop-after`）；拒绝路径在协议层实测，UI 按钮走同一代码路径。
- [ ] Codex：完成一次含命令与文件改动的轮次，diff 与计划卡正确渲染。
- [ ] 同一窗口内两种后端会话并存，互不串流。
- [ ] 手动杀掉后端进程，界面在 2 秒内进入失败态且不卡死。
- [x] 每个审批请求都有终态应答：夹具与端到端用例都断言了允许、拒绝两条路径，且同一 `request_id` 不可重复应答。
- [x] 退出应用后无残留 `claude` 进程（实测应用退出后 `claude` 进程数为 0）。

## 完成定义

两个适配器通过各自夹具测试，并在真实后端上跑通「新建 → 多轮工具调用 → 审批 → 中断 → 恢复」全链路，
且归一化事件模型未因某个后端而新增特例字段。
