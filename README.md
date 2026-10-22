# Cirno

一个 Windows 原生的 Agent 座舱：在一个 WinUI 3 窗口里同时驱动多个 Claude Code / Codex 会话。

## 这是什么？

- **定位**：单机桌面应用（Windows 11），面向个人开发者，不做账号体系与云服务。
- **解决的核心问题**：终端一次只能看一个会话，Electron 壳又改不动；后台任务的进度、审批与结果在系统层完全不可见。

## 为什么存在？

- 多任务并行时，「哪个会话在等我审批」应该一眼可见，而不是切窗口找。
- 每个会话用什么模型、走哪个 provider，界面上必须如实显示。
- Windows 上的原生姿态（Mica、任务栏、托盘、键盘操作）是终端与 Electron 都给不了的。
- 主题想要个性就可以有，但默认应当是最干净、最不干扰阅读的样子。

## 如何安装和运行？

**前置要求**

- Windows 11（22000+，Mica 需要；更低版本降级为纯色）
- 运行：无（安装包自包含运行时）
- 开发：.NET 10 SDK、Node.js 20+、pnpm；以及本机已安装 `claude`（Claude Code）与/或 `codex` CLI

**安装（用户）**

- 双击 `Cirno-<version>-x64.msi`，缺系统依赖时安装器会引导补齐。

**安装（开发）**

```bash
pnpm install
```

**运行 / 构建**

```bash
pnpm theme            # 构建主题资源（取色 → Theme.xaml + 资产，输出到 artifacts/themes/）
pnpm theme:preview    # 预览取色结果
pnpm theme:all        # 重建全部主题

dotnet build apps/Cirno.slnx -p:Platform=x64
# 运行（非打包，输出目录下的 Cirno.exe）
./src/Cirno.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Cirno.exe
```

启动参数：

| 参数 | 用途 |
|---|---|
| `--theme <id>` | 以指定主题启动（如 `--theme dafeiyu`） |
| `--cwd <dir>` | 指定会话工作目录；不填则用当前目录 |
| `--prompt <text>` | 开发自检：启动后立即发送一条消息 |
| `--auto-approve` | 开发自检：自动放行审批（无人值守跑通链路用） |
| `--trace <file>` | 开发自检：把归一化事件写成 JSONL，便于断言 |
| `--stop-after <秒>` | 开发自检：发送后等待 N 秒自动中断 |
| `--new-session` | 开发自检：忽略历史，强制新建会话 |
| `--parallel <N>` | 开发自检：并发 N 个会话发同一问题 |
| `--restore-check <file>` | 开发自检：把还原结果写成 JSON |
| `--provider <id>` | 新建会话默认使用的后端 |

## 当前状态

- **阶段**：M2（多会话）已跑通。侧边栏多会话、会话级 provider/model、SQLite 持久化、重启还原、并发 3 会话不串流均已实测。
- **已知限制**：Codex 后端与任务栏/托盘状态未接入（M3/M4）；Markdown 仅支持子集（围栏代码块、标题、列表、加粗、行内代码），表格/链接/图片按纯文本呈现；Job Object 托管待 M4。
- 开发环境注意：需要 .NET 10 SDK；`claude` CLI 由用户自装，可用 `CIRNO_CLAUDE_PATH` 指定路径。

## 核心技术

- **运行期**：C# / .NET 10、WinUI 3（Windows App SDK 2.x）、Fluent 2、Mica、CommunityToolkit.Mvvm、SQLite、CsWin32 / Win2D。
- **构建期**：Node.js / TypeScript（主题管线）、sharp（取色与位图转码）。
- **后端**：Claude Code CLI（stream-json）、Codex app-server（JSON-RPC over stdio）。
- **分发**：MSI（自包含发布 + 依赖自举）。

---

文档入口：`docs/PRD.md`（做什么）、`docs/ARCHITECTURE.md`（怎么组织）、`docs/DECISIONS.md`（为什么）、`docs/specs/`（模块契约）。
