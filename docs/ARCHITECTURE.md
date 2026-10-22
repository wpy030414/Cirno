# ARCHITECTURE — Cirno

## 系统概述

单进程 WinUI 3 桌面应用：XAML 壳负责呈现，Core 负责会话编排与持久化，后端适配器负责把两种 Agent 的 stdio 协议翻译成同一套事件模型；皮肤/主题是构建期产物，运行期只是资源字典。

```
┌───────────────────────────────────────────────────────────┐
│ Cirno.App（WinUI 3 / Fluent 2 / Mica）                     │
│   标题栏 · 侧边栏 · 时间线 · Composer · 详情面板 · 主题层    │
└──────────────────────┬────────────────────────────────────┘
                       │ ViewModel（UI 线程，30Hz 合帧）
┌──────────────────────▼────────────────────────────────────┐
│ Cirno.Core                                                │
│   SessionOrchestrator · SessionStore(SQLite) ·             │
│   ProviderRegistry · AgentEventBus                         │
└──────────────────────┬────────────────────────────────────┘
                       │ 归一化事件模型（IAgentBackend）
┌──────────────────────▼────────────────────────────────────┐
│ Cirno.Agents.Abstractions                                 │
│   SessionInit · Turn · Item(started/delta/completed) ·     │
│   Approval · Notice · Error                                │
└──────────┬─────────────────────────────────┬──────────────┘
┌──────────▼─────────────┐        ┌──────────▼──────────────┐
│ Cirno.Agents.Claude    │        │ Cirno.Agents.Codex      │
│ 1 会话 = 1 进程         │        │ 1 app-server = N thread │
└──────────┬─────────────┘        └──────────┬──────────────┘
           │ spawn                            │ spawn
     claude CLI（用户自装）            codex app-server

旁挂：Cirno.Shell.Windows（任务栏/托盘/Job Object）、
      Cirno.Markdown（Markdig → XAML）、
      tools/theme（构建期：取色 → tokens → Theme.xaml）
```

## 核心模块

| 模块 | 职责 |
|---|---|
| `Cirno.App` | WinUI 3 可执行；窗口、视图、ViewModel、主题装配；不直接碰子进程 |
| `Cirno.Core` | 会话生命周期与并发编排、审批路由、SQLite 持久化、provider 与凭据登记 |
| `Cirno.Agents.Abstractions` | 后端无关的会话/轮次/条目/审批模型与 `IAgentBackend` 契约；零第三方依赖 |
| `Cirno.Agents.Claude` | Claude Code 的 stream-json 与 control 协议适配 |
| `Cirno.Agents.Codex` | Codex app-server 的 JSON-RPC 适配 |
| `Cirno.Shell.Windows` | 任务栏/托盘/跳转列表、子进程 Job Object、窗口与材质辅助；不引用 Core |
| `Cirno.Markdown` | Markdown AST → XAML Inlines/Blocks，含代码块与表格 |
| `tools/theme` | 构建期主题管线：插画取色 → 46 token → `Theme.xaml` + 位图资产 |
| `themes/<id>.theme` | 主题数据：manifest（角色映射、取色来源、展示名）+ 源图 |

## 模块关系

- 依赖方向单向：`App → Core → Abstractions ← Agents.*`；`Agents.*` 之间互不引用，新后端只加一个适配器。
- `Shell.Windows` 只暴露服务接口，由 App 组装；它不知道「会话」是什么，只接收聚合后的状态。
- `Core` 不认识任何具体协议字段：所有差异在适配器里被消化成归一化事件。
- 构建期与运行期彻底分离：`tools/theme` 只在构建时运行，产出物是 XAML 与图片，应用不依赖 Node。

## 数据流

**构建期（主题）**

```
themes/<id>.theme/manifest.json（roles + colorSource + displayName）
  → 取色（k-means++，确定性）
  → 46 token × { light, dark }
  → Theme.xaml（ThemeDictionaries: Default=暗 / Light / HighContrast，含 Color 与 Brush 双键 + Cirno* 全量调色板）
  → Assets/*（背景 JPG、立绘 PNG）
  → 主题注册表（App 的主题选择器读取）
```

**运行期（会话）**

```
用户输入 → Composer → Orchestrator → IAgentBackend.SendAsync
  → 子进程 stdout（JSONL / JSON-RPC）
  → 适配器解析为归一化事件
  → SessionStore 落库（条目级）+ AgentEventBus 合帧
  → ViewModel 增量更新时间线
  → 会话状态变化 → 任务栏/托盘状态聚合（去抖后统一刷新）
```

**审批回流**

```
后端请求审批 → 适配器发 ApprovalRequested → Core 挂起该会话的运行态
  → UI 内联审批卡（同时任务栏图标变化）
  → 用户决策 → ApprovalResolved → 适配器把决策编码成对应后端的应答格式
```

## 外部系统

| 外部系统 | 关系 |
|---|---|
| `claude` CLI | 用户自行安装；Cirno 只负责按会话启动、喂输入、读输出、应答审批 |
| `codex app-server` | 同上；一个实例承载多个 thread |
| Windows Shell（任务栏 / 托盘 / 跳转列表） | 通过 `ITaskbarList3`、通知区图标、`ICustomDestinationList` 交互 |
| Windows 凭据管理器 | 存放 provider 的 API Key / token，绝不落库明文 |
| 文件系统 / `cwd` | 每个会话绑定一个固定工作目录，这是后端定位历史与工程语义的前提 |

## 关键子系统

### 会话与并发

- 状态机：`Idle → Running → (AwaitingApproval ⇄ Running) → Idle | Failed | Interrupted`。
- 同一 provider 下 Codex 会话共享一个 app-server；Claude 会话各自一进程（进程即会话）。
- 默认最多 4 个会话同时运行，其余排队并显示「等待中」。
- 关闭窗口不杀进程：后台继续跑；应用退出时由 Job Object 兜底清理子进程树。

### 渲染与性能

- 时间线为虚拟化列表，只实例化可视区附近条目。
- 流式增量不逐 token 刷新 UI：写入缓冲后按固定帧率合帧提交。
- Markdown 按块渲染：未闭合代码块走纯文本快速路径，闭合后再高亮。
- 目标：冷启动 < 1.5s 可交互；万条消息滚动不掉帧；材质不可用时降级为纯色不崩。

### 审批

- 审批卡内联在时间线中，不用模态对话框打断上下文。
- 决策归一化为 `Allow / AllowForSession / Deny / Cancel`；后端独有的扩展（如按命令放行并记忆）作为附加选项呈现。
- 每个审批请求必须有终态应答——否则后端工具调用会永久阻塞。

### 主题

- 默认主题不加载任何图片，完全由 Fluent 资源与 Mica 构成。
- 插画主题由构建期产物装配：氛围背景（低不透明度）、两侧立绘（贴底、不遮挡交互）、内容遮罩。
- 明/暗/高对比度三套字典齐备；高对比度下退回系统颜色，不做品牌着色。
- 运行时切换主题 = 换资源字典 + 换装饰层，不重启应用。

### 系统级状态

- 任务栏：进度态映射会话聚合状态（运行中 → `Indeterminate`、待审批 → `Paused`、失败 → `Error`、空闲 → `NoProgress`），
  待审批数量叠加为徽标图标。实现挂在窗口子类化上，收到 `TaskbarButtonCreated` 才初始化 `ITaskbarList3`。
- 托盘常驻：显示窗口、新建会话、退出；提示文案随聚合状态变化。
- 跳转列表：最近会话直达（**尚未实现**，M4 只做了任务栏与托盘）。
- 所有 Shell 调用都必须在窗口的任务栏按钮就绪之后进行（需要挂接窗口消息，而不是在窗口激活时抢先调用）。
- 子进程由 Job Object（`KILL_ON_JOB_CLOSE`）托管：应用崩溃时整棵进程树被内核回收。

### 持久化

- SQLite（WAL）：`sessions`（元数据、后端会话 id、provider、cwd）+ `items`（归一化条目，按 seq 排序）。
  库文件在 `%LOCALAPPDATA%\Cirno\cirno.db`，可整体删除即回到干净状态。
- 后端历史仍是恢复真相源；本地库是渲染与检索用的副本，可从后端历史重建。
- 还原是惰性的：启动只读会话列表，点开某个会话才读它的条目；不启动任何后端进程，
  首次发送时才按 `ResumeSessionId` 恢复。
- 凭据单独存放于 Windows 凭据管理器。

### Provider 与模型

- provider 是**进程级**配置：Claude 走环境变量，Codex 走其自身配置目录。
- 因此「切换 provider」等于换进程，而不是运行期开关——UI 表现为会话属性，实现是进程池键。
- 模型清单：Codex 由后端提供；Claude 无清单能力，用配置 + 后端回报的实际模型双向展示。

## 重要技术边界

1. **不修改后端行为**：不伪造工具结果、不改写模型请求、不注入系统提示。
2. **不做协议抽象泄漏**：归一化模型之外，界面不得出现后端字段名。
3. **未知事件不丢弃**：无法识别的条目降级展示原始数据，保证后端演进不炸 UI。
4. **无远程依赖**：主题素材全部本地内联打包，离线可用；无遥测与自动更新。
5. **单实例单进程**：不做多进程架构；子进程只由 Core 托管。
6. **权限最小化**：只在用户目录安装/写入（MSI 场景按机器安装），不修改系统主题与任务栏设置。
