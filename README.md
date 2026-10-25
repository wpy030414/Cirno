# Cirno

一个 Windows 原生的 Agent 座舱：在一个 WinUI 3 窗口里同时驱动多个 Claude Code / Codex 会话。

## 这是什么？

- **定位**：单机桌面应用（Windows 11），面向个人开发者，不做账号体系与云服务。
- **解决的核心问题**：终端一次只能看一个会话，Electron 壳又改不动；后台任务的进度、审批与结果在系统层完全不可见。

## 为什么存在？

- 多任务并行时，「哪个会话在等我审批」应该一眼可见，而不是切窗口找。
- 每个会话绑定一个工作目录；目录在会话开始时确定，之后不可更改，界面上只保留一个打开目录的入口。
- 模型是会话属性，由后端配置或启动参数决定；主界面不提供运行期选择器（见 ADR-134）。
- Windows 上的原生姿态（Mica、任务栏、托盘、键盘操作）是终端与 Electron 都给不了的。
- 主题想要个性就可以有，但默认应当是最干净、最不干扰阅读的样子。

## 如何安装和运行？

**前置要求**

- Windows 11（22000+，Mica 需要；更低版本降级为纯色）
- 运行：无（安装包自包含运行时）
- 开发：.NET 10 SDK、Node.js 20+、pnpm；以及本机已安装 `claude`（Claude Code）与/或 `codex` CLI

**安装（用户）**

- 双击 `Cirno-<version>-x64.msi`：按用户安装到 `%LOCALAPPDATA%\Programs\Cirno`，不需要管理员。
- .NET、Windows App SDK、VC++ 运行库都已随包，目标机器无需预装任何运行时。
- 卸载默认保留会话数据（`%LOCALAPPDATA%\Cirno`），需要彻底清除时手工删除该目录。

**安装（开发）**

```bash
pnpm install
```

**运行 / 构建**

```bash
pnpm theme            # 构建主题资源（取色 → Theme.xaml + 资产，输出到 artifacts/themes/）
pnpm theme:preview    # 预览取色结果
pnpm theme:all        # 重建全部主题

dotnet build Cirno.slnx
dotnet test Cirno.slnx        # 协议夹具 + 存储层性能护栏
# 存储层基准（万条消息读写耗时）
dotnet run --project tools/perf -- --items 10000
# 打 MSI（自包含发布 → 组件清单 → WiX 打包，约 82MB）
#   tools\installer\build-msi.ps1
# 运行（非打包，解决方案构建输出目录下的 Cirno.exe）
./src/Cirno.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Cirno.exe
```

启动参数：

| 参数 | 用途 |
|---|---|
| `--theme <id>` | 以指定主题启动（如 `--theme dafeiyu`） |
| `--cwd <dir>` | 新建会话草稿的初始工作目录；不填则沿用最近会话或用户目录 |
| `--prompt <text>` | 开发自检：启动后立即发送一条消息 |
| `--auto-approve` | 开发自检：自动放行审批（无人值守跑通链路用） |
| `--trace <file>` | 开发自检：把归一化事件写成 JSONL，便于断言 |
| `--stop-after <秒>` | 开发自检：发送后等待 N 秒自动中断 |
| `--new-session` | 忽略历史，进入新建会话草稿（带 `--prompt` 时发送即落库） |
| `--model <id>` | 新建会话使用的模型（模型没有界面选择器，这是唯一入口） |
| `--parallel <N>` | 开发自检：并发 N 个会话发同一问题 |
| `--restore-check <file>` | 开发自检：把还原结果写成 JSON |
| `--shutdown` | 安装器用：请求正在运行的主实例优雅退出 |
| `--perf <N>` | 开发自检：造 N 条消息，测还原/绑定/滚动/内存，报告写 `--perf-report` |

## 仓库结构

```
Cirno/
├── Cirno.slnx                 解决方案（/src/ /tests/ /tools/ 分组；App 映射 x64）
├── Directory.Build.props      共享构建属性；CalVer 版本号唯一权威
├── nuget.config               构建期 NuGet 源
├── .github/workflows/         CI：构建 + 测试 + MSI + portable zip + Release
├── src/Cirno.App/             WinUI 3 壳：窗口、视图、ViewModel、主题装配
├── src/Cirno.Core/            会话编排与 SQLite 持久化
├── src/Cirno.Agents.*/        后端适配器（Abstractions 契约 + Claude 实现）
├── src/Cirno.Shell.Windows/   任务栏、托盘、跳转列表、单实例、Job Object
├── tests/                     协议夹具回放、存储层护栏、假 Claude CLI
├── tools/theme/               构建期主题管线（Node/TS）
├── tools/perf/                存储层基准
├── tools/installer/           MSI 定义与构建脚本
├── themes/                    主题数据
└── docs/                      PRD / ARCHITECTURE / DECISIONS / specs
```

- 依赖方向单向：`App → Core → Abstractions ← Agents.*`。
- 版本号 **CalVer `YY.MM.DD-HH`**，`Directory.Build.props` 的 `<Version>` 是唯一权威；
  三段（`26.10.26`）或点分四段（`26.10.26.01`，同日多次发布）均可，MSI 的 ProductVersion 由安装脚本取前三段。

## 打包与发布（CI）

- `.github/workflows/windows.yml`：单一 `windows-latest`。
  - push `main` / PR → 主题构建与测试 + restore + 构建 + 协议测试 + MSI + portable zip。
  - push `main` → 额外发布 Release（tag `v<version>`，同名先删后建，保持幂等）。
- 版本号只读 `Directory.Build.props`；产物名、MSI ProductVersion 与 Release tag 全部由它派生，CI 不写版本。

## 当前状态

- **阶段**：M7（质量基线）已跑通 —— 协议有夹具回放测试、单实例转交已验证、万条消息性能有基准数字。
- M6（分发）已跑通：MSI 装完即可启动，卸载干净并保留用户数据；升级时自动关闭运行中的实例。
- M5（打磨）已跑通：文件变更按行着色（新增绿 / 删除红）、流式期间走纯文本快路径、键盘全流程。
- **已知限制**：Codex 后端按用户决定暂缓（M3）；Markdown 仅支持子集（围栏代码块、标题、列表、加粗、行内代码），表格/链接/图片按纯文本呈现；安装包未做代码签名。

**快捷键**

| 快捷键 | 动作 |
|---|---|
| `Enter` / `Shift+Enter` | 发送 / 换行 |
| `Ctrl+N` | 进入新建会话草稿 |
| `Ctrl+1` … `Ctrl+9` | 切换到第 N 个会话 |
| `Ctrl+K` | 聚焦输入框 |
| `Esc` | 中断当前轮次 |
- 开发环境注意：需要 .NET 10 SDK；`claude` CLI 由用户自装，可用 `CIRNO_CLAUDE_PATH` 指定路径。

## 核心技术

- **运行期**：C# / .NET 10、WinUI 3（Windows App SDK 2.x）、Fluent 2、Mica、CommunityToolkit.Mvvm、SQLite、CsWin32 / Win2D。
- **构建期**：Node.js / TypeScript（主题管线）、sharp（取色与位图转码）。
- **后端**：Claude Code CLI（stream-json）、Codex app-server（JSON-RPC over stdio）。
- **分发**：MSI（自包含发布 + 依赖自举）。

---

文档入口：`docs/PRD.md`（做什么）、`docs/ARCHITECTURE.md`（怎么组织）、`docs/DECISIONS.md`（为什么）、`docs/specs/`（模块契约）。
