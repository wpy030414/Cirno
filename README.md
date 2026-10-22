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
pnpm theme            # 构建主题资源（取色 → Theme.xaml + 资产）
pnpm theme:preview    # 预览取色结果
pnpm theme:all        # 重建全部主题

# 应用本体（M0 进行中）
dotnet build apps/Cirno.slnx
```

## 当前状态

- **阶段**：重构期（M0）。Electron 皮肤引擎已删除，应用外壳尚未落地。
- 已就绪：主题管线（取色 → 46 token → WinUI 资源字典 → 位图资产 → 主题注册表）。
- **已知限制**：应用本体、会话管理、两个后端适配器、任务栏集成均未实现；详见 `docs/PRD.md` 的里程碑。
- 本机开发环境注意：需要安装 .NET 10 SDK（当前机器只有运行时）。

## 核心技术

- **运行期**：C# / .NET 10、WinUI 3（Windows App SDK 2.x）、Fluent 2、Mica、CommunityToolkit.Mvvm、SQLite、CsWin32 / Win2D。
- **构建期**：Node.js / TypeScript（主题管线）、sharp（取色与位图转码）。
- **后端**：Claude Code CLI（stream-json）、Codex app-server（JSON-RPC over stdio）。
- **分发**：MSI（自包含发布 + 依赖自举）。

---

文档入口：`docs/PRD.md`（做什么）、`docs/ARCHITECTURE.md`（怎么组织）、`docs/DECISIONS.md`（为什么）、`docs/specs/`（模块契约）。
