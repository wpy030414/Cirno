# AGENTS.md

Cirno 是一个 WinUI 3 原生 Agent 座舱；本文件定义 Agent 在本仓库工作时的边界与约定。

## 概述

- 本项目是什么：Windows 11 桌面应用，支持 Claude Code 与 Codex 两种后端，多会话并行、会话级 provider/model、可切换主题、系统级状态呈现。
- 形态：单应用 + 一个构建期主题工具；不再是多 target 的皮肤 monorepo。

## 边界与范围

**范围内**

- `apps/` 下的 WinUI 3 应用、`src/` 下的 Core 与后端适配器、`tools/theme` 主题管线、`themes/` 主题数据。
- 文档：PRD / ARCHITECTURE / DECISIONS / specs。

**非目标（明确排除）**

- 各后端的 skill / plugin / MCP 管理（只透传后端自身配置，不呈现、不管理）。
- 定时任务、资料库、云同步、账号体系、遥测、自动更新探测。
- 自研模型 API 客户端、跨平台、多窗口/标签页布局。
- 给第三方 Electron 应用打补丁（该路线已废弃，见 ADR-108）。

## Agent 操作指南

**如何理解本项目**

- 先读 `docs/PRD.md` 确认要做什么，再读 `docs/ARCHITECTURE.md` 确认结构，改行为前查 `docs/DECISIONS.md` 避免推翻既有结论。
- 模块级实现契约在 `docs/specs/`；新增模块时同步新增 spec。

**全局规则**

- 文档用简体中文；决策记录用 ADR 格式，编号从 ADR-101 起（ADR-001~026 属已删除的皮肤引擎阶段，不要占用这些编号）。
- 分层依赖单向：`App → Core → Abstractions ← Agents.*`。`Agents.*` 之间不得互相引用；新增后端只加适配器，不改 Core 与 UI。
- 归一化事件模型是硬边界：界面层不得出现后端专有字段名；无法识别的条目降级展示，不得丢弃。
- 每个审批请求必须有终态应答，否则后端工具调用会永久阻塞。
- 颜色一律来自 token；源码中不得硬编码色值。带 alpha 的颜色只允许出现在组件级调色板。
- `artifacts/`、`tools/theme/dist/` 为构建产物，不得手工修改，也不纳入版本控制。
- 新增主题 = 新建 `<id>.theme/` 目录 + `manifest.json` + 素材，不改代码。
- 不引入远程资源依赖：素材本地内联，离线可用。
- 不修改后端行为：不伪造工具结果、不改写模型请求、不注入系统提示。
- 子进程最终必须由 Job Object 托管，保证应用崩溃退出时无残留进程；M1 阶段以「会话销毁时关闭 stdin + 杀进程树」兜底，Job Object 随 `Cirno.Shell.Windows` 在 M4 落地。
- `src/Cirno.Agents.*` 的目标框架必须是 `net10.0`（不得引用 WinUI / Windows App SDK），从编译期守住「适配器不碰 UI」。
- Claude 审批通道依赖两个开关缺一不可：`--permission-prompt-tool stdio` + host 先发 `initialize` 控制请求。缺少任一项，工具调用会被静默拒绝——改动前先看 `docs/specs/module-agent-protocol.md`。

**验证**

```bash
pnpm theme                          # 主题产物可生成、XML 合法、对比度闸门通过
pnpm test:theme                     # 取色算法单测
dotnet build apps/Cirno.slnx -p:Platform=x64   # 应用可编译
```

- 会话 id 必须是标准 UUID（带连字符）：Claude 的 `--session-id` 会校验格式，无连字符会被后端直接拒启。
- 端到端自检（真实后端，会消耗 token）：
  ```bash
  Cirno.exe --cwd <dir> --prompt "读 hello.txt 并把行序反转后写入 out.txt" \
            --trace <dir>\trace.jsonl [--auto-approve] [--stop-after 8]
  ```
  用 `--trace` 产出的 JSONL 断言事件序列，比肉眼可靠。

- SDK 版本由根目录 `global.json` 锁定在 10.0.4xx；若 SDK 装在用户目录（如 `C:\Users\<u>\.dotnet`），需把该目录加入 PATH，或直接用其绝对路径调用 `dotnet.exe`。
- 启动验证：`Cirno.exe --theme dafeiyu` 可直接以插画主题启动，用于无人值守校验主题包。

## 目录速查

- `README.md` — 项目速览与上手
- `AGENTS.md` — 本文件：Agent 边界与约定
- `docs/PRD.md` — 做什么、给谁、不做什么、里程碑
- `docs/ARCHITECTURE.md` — 系统结构、模块职责、数据流、技术边界
- `docs/DECISIONS.md` — 决策记录（ADR-101 起；旧阶段内容已删除，追溯看 git）
- `docs/specs/module-agent-protocol.md` — 两个后端协议的适配契约与归一化事件模型
- `docs/specs/module-theme-pipeline.md` — 取色 → token → Theme.xaml 的产物契约
- `docs/specs/module-packaging.md` — MSI 分发与依赖自举行为
- `tools/theme/` — 构建期主题管线（Node/TS）
- `themes/<id>.theme/` — 主题数据：manifest + 源图（纯源文件，不含产物）
- `apps/` — 解决方案文件
- `src/Cirno.App/` — WinUI 3 可执行：窗口、视图、ViewModel、主题装配
- `src/Cirno.Core/` — 会话编排（`SessionOrchestrator`）与 SQLite 持久化（`SessionStore`）
- 外壳状态用 `--trace` 断言：日志里 `evt=ShellState` 会带 `taskbar-ready` / `progress-ok` / `badge` / `overlay-ok` / `tray-visible` / `job-assign`。
- `src/Cirno.Agents.Abstractions/` — 归一化事件模型与 `IAgentBackend` 契约（零 UI 依赖）
- `src/Cirno.Agents.Claude/` — Claude Code stream-json 与 control 协议适配
- `src/Cirno.Agents.Codex/` — Codex app-server 适配（M3，按用户决定暂缓）
- `src/Cirno.Shell.Windows/` — 任务栏、托盘、Job Object；不引用 Core，也不知道「会话」是什么
