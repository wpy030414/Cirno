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

- 界面新建会话以草稿态存在（ADR-133）：只清空当前选择并记录草稿目录，不写库、不进侧栏；
  草稿目录默认为用户主目录（`~`），可在 Composer 里改（ADR-136）；
  首条非空消息发送时才 `CreateSession`，再进入下面的落库/启动流程。
- 创建会话只落库、不启动进程；首次发送才启动（`EnsureStartedAsync`）。
- 恢复：把 `backend_session_id` 作为 `ResumeSessionId` 传给适配器；由后端负责续接历史。
- 并发上限 4；超出的会话进入 `Queued`（界面显示「等待中」），空位释放后自动继续。
- 同一会话的发送串行化，避免用户连击导致两条 user 消息交错写入。
- 首个用户消息自动生成标题（折叠换行、截断 42 字），写入后侧边栏立即刷新。
- 删除：先释放进程（回绝挂起审批），再把历史转录移入回收站，最后删库中该会话的 `sessions` 与 `items`；
  回收站失败则中止（库保留，可重试）。
- 应用退出：释放所有会话（关 stdin、必要时杀进程树）。

### 会话列表：历史目录为权威源（ADR-132）

- 列表数据来自各后端 `IAgentHistorySource.ListSessions()` 的元数据扫描（Claude：`~/.claude/projects`，
  `CLAUDE_CONFIG_DIR` 可重定向根），**不做全量解析**——每文件只窥读前 64KB。
- 扫描提取：会话 id（文件名 UUID）、cwd（行内字段，缺时回退 munged 目录名）、首时间戳、
  标题（ai-title > summary > 首条人类消息截 42 字，机器块已清洗）、最后活动时间（文件 mtime）。
- 合并规则：目录摘要 + 本地库运行期字段（model/title）；库中有而目录中没有的会话不列出（条目留库）；
  pending 会话（`backend_session_id` 为空、尚无转录文件）**且已有条目**时从库保留进列表；
  零条目的空壳不再列出（ADR-133）。
- 排序：最后活动时间倒序。

### 导入与重导入

- 触发条件（`LoadItemsAsync`，惰性）：库无该会话记录、或库中条目数为 0、
  或转录文件 mtime 晚于库 `updated_at`（会话在别处继续过）。
- 活会话（进程在跑）不导入——live 事件正在实时落库，重导入会与之竞态。
- 导入语义：`ReadTranscript` 全量解析 → `UpsertSession` 回填 cwd/标题/后端 id → `ReplaceItems`
  单事务 DELETE+INSERT 整体重建。幂等建立在整体重建上：live 的 user 条目 id 与转录行 uuid 不同源，
  按 item_id 去重不可靠。
- 转录解析走 `ClaudeEventParser` 转录模式（`EmitUserMessages`），sidechain、isMeta 与机器块行
  （斜杠命令回显 `<command-name>`/`<command-message>`/`<command-args>`、`<local-command-*>`、`<system-reminder>`）剔除，
  正文里的机器块也会被剥离（ADR-135）；
  条目时间戳取自行内 `timestamp`；协议形状由 `tests/fixtures/transcript-*.jsonl` 承载（ADR-124）。
- 全量解析补标题：64KB 扫描没拿到标题时，用首条人类 `UserMessage` 回填 `transcript.Summary.Title`，
  导入后触发 `SessionChanged`，侧栏无需重启即可刷新。

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
- 删除正在运行的会话 → 先回绝挂起审批并结束进程，转录入回收站，再删记录。
- 历史根目录随 `CLAUDE_CONFIG_DIR` 变化 → 权威源跟着当前根走，旧根下的会话自然不可见（行为即设计）。
- 转录文件在列出与点开之间被外部删除 → 时间线保持为空，不报错。
- 导入失败（转录损坏/被占用）→ 时间线显示错误条目、会话进入失败态，界面不崩。
- 64KB 窥读窗口内拿不到 cwd 或标题 → 回退 munged 目录名 / 无标题；极端长前导噪声属可接受损耗。

## 验收标准

- [x] 两个会话分别在不同目录运行，重启后列表与时间线完整还原（实测 `--restore-check` 报告 2 会话各 2 条）。
- [x] 3 个会话并发运行，事件互不串流，各自 `stop_reason=end_turn`（实测 `--parallel 3`）。
- [x] 会话标题取自首条人类消息（斜杠命令回显等机器块已清洗，ADR-135）。
- [x] 历史导入不把斜杠命令回显写成用户气泡（`transcript-command-echo.jsonl` 夹具覆盖）。
- [x] 新建会话在首条消息发出前不落库、不进侧栏（UI 自动化实测：草稿态列表保持 14 项，发送后 15 项）。
- [x] 退出后无残留 `claude` 进程。
- [x] 终端里用 CLI 创建的历史会话出现在列表（`SessionOrchestratorHistoryTests` + `ClaudeHistorySourceTests` 覆盖）。
- [x] 打开未导入会话导入且续聊走 `--resume`；文件更新后重导入无重复行（测试覆盖）。
- [x] 删除会话：转录进回收站、库清空；回收站失败时库保留可重试（测试覆盖）。
- [ ] 库文件损坏时的降级路径（未构造用例）。
- [ ] 删除运行中的会话（未构造用例）。

## 性能预算

万条消息是本项目的规模上限（见 ADR-128）。预算写在测试里，不是写在文档里。

| 指标 | 预算 | 实测（10k 条） |
|---|---|---|
| 批量写入 | < 2000 ms | ~280 ms |
| 读取还原 | < 1000 ms | ~45 ms |
| 界面还原（`--perf`） | — | ~34 ms |
| 界面绑定 | — | ~21 ms |
| 滚动到最后一条 | — | ~30 ms |
| 滚动往返（中段↔末尾） | — | ~28 ms |
| 托管堆 | — | ~7 MB |

- 序号在内存里递增（`_nextSeq`），不每条查 `MAX(seq)`——后者是 O(n²)，会把写入拖到秒级。
- 批量写入用单事务包住（`AppendItems`）；流式输出仍走逐条 `AppendItem`，那条路径写量小、延迟低。
- 应用退出前 `Checkpoint()` 把 WAL 落回主库，下次打开不必回放大量日志。
- 历史还原走 `BulkObservableCollection.AddRange`，一次 Reset 通知，而不是逐条 Add。

## 完成定义

多会话在真实后端上并发跑通，重启可还原，且界面层只通过编排器接口访问会话。
