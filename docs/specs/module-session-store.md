# Spec — 会话存储与编排

## 要构建什么

`Cirno.Core` 的两个部件：`SessionStore`（本地归一化存储）与 `SessionOrchestrator`（会话生命周期与并发）。
判定标准：界面不直接触碰 SQL 或子进程；新增后端只需注册一个 `IAgentBackend` 实现。

## 行为

### 存储

- 位置：`%LOCALAPPDATA%\Cirno\cirno.db`，WAL 模式。删除该目录即回到全新状态。
- `sessions`：`id`（也是本地主键与后端会话 id）、`provider_id`、`model`、`working_directory`、
  `title`、`backend_session_id`、`created_at`、`updated_at`。
- `items`：`session_id` + `item_id` 复合主键、`seq`（单调递增，还原顺序）、`kind`、`text`、
  `detail`、`tool_name`、`tool_use_id`、`is_error`、`timestamp`。
- 写入时机：条目**完成**时落库（流式中间态不写），同 id 覆盖（幂等），`seq` 保持不变。
- 一个连接 + 锁串行化：写量小（条目级），换取顺序确定与实现简单。

### 编排

- 创建会话只落库、不启动进程；首次发送才启动（`EnsureStartedAsync`）。
- 恢复：把 `backend_session_id` 作为 `ResumeSessionId` 传给适配器；由后端负责续接历史。
- 并发上限 4；超出的会话进入 `Queued`（界面显示「等待中」），空位释放后自动继续。
- 同一会话的发送串行化，避免用户连击导致两条 user 消息交错写入。
- 首个用户消息自动生成标题（折叠换行、截断 42 字），写入后侧边栏立即刷新。
- 删除：先释放进程（回绝挂起审批），再删库中该会话的 `sessions` 与 `items`。
- 应用退出：释放所有会话（关 stdin、必要时杀进程树）。

## 输入 / 输出

**输入**：`providerId`、`workingDirectory`、可选 `model`；用户消息文本；审批决策；中断请求。

**输出**：`SessionEvent(sessionId, AgentEvent)`、`SessionChanged(sessionId)`、`SessionStateChanged(sessionId, state)`。

## 约束

- 会话 id 必须是标准 UUID（带连字符）：Claude 的 `--session-id` 会校验格式。
- `SessionStore` 不做业务判断：不生成标题、不解释事件类型。
- 编排层不得出现后端专有字段；后端差异只在适配器里。
- 还原必须是惰性的：启动不读全量条目、不启动进程；会话多时也不能拖慢冷启动。

## 边界条件

- 库文件损坏或缺表 → 启动时重建空库，不阻塞应用（当前策略：建表语句均为 `IF NOT EXISTS`）。
- 后端恢复失败（会话已被后端清理）→ 会话进入失败态，保留本地时间线，允许继续提问。
- 同一会话被两个窗口同时打开 → 不做多实例保护（单实例应用前提下暂不需要）。
- 删除正在运行的会话 → 先回绝挂起审批并结束进程，再删记录。

## 验收标准

- [x] 两个会话分别在不同目录运行，重启后列表与时间线完整还原（实测 `--restore-check` 报告 2 会话各 2 条）。
- [x] 3 个会话并发运行，事件互不串流，各自 `stop_reason=end_turn`（实测 `--parallel 3`）。
- [x] 会话标题取自首条用户消息。
- [x] 退出后无残留 `claude` 进程。
- [ ] 库文件损坏时的降级路径（未构造用例）。
- [ ] 删除运行中的会话（未构造用例）。

## 完成定义

多会话在真实后端上并发跑通，重启可还原，且界面层只通过编排器接口访问会话。
