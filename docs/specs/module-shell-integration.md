# Spec — Windows 外壳集成

## 要构建什么

`Cirno.Shell.Windows`：把「会话状态」翻译成 Windows 外壳呈现（任务栏进度、叠加徽标、托盘图标），
并托管子进程。它不引用 `Cirno.Core`，也不知道「会话」是什么——只接收聚合后的四个数字。

## 行为

### 任务栏

- 入口消息：`RegisterWindowMessage("TaskbarButtonCreated")`；在窗口子类化里收到该消息后才初始化 `ITaskbarList3`。
- 状态映射（优先级从上到下）：待审批 → `Paused`；失败 → `Error`；运行中 → `Indeterminate`；其余 → `NoProgress`。
- 待审批数量 > 0 时设置叠加徽标（圆形 + 数字，>9 显示 `9+`），描述文案为「N 个会话等待审批」；归零时清除徽标。
- 所有 COM 调用失败都只降级（不抛、不阻塞），并在诊断通道记录 `ok=False`。

### 托盘

- `Shell_NotifyIconW` 添加图标；左键/右键都弹出原生菜单：显示 Cirno / 新建会话 / 退出。
- 提示文案随状态变化（「就绪」「N 个运行中」「N 个待审批」）。
- 弹菜单前必须 `SetForegroundWindow`，菜单结束后 `PostMessage(WM_NULL)`，否则点空白处菜单不消失。

### 子进程托管

- 一个 Job Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）承载所有后端进程。
- 后端进程启动后立刻按 PID 加入作业；应用退出时关闭作业句柄 = 内核回收整棵进程树。
- 加入失败（例如进程已在别的作业中）只记录，不影响会话本身。

## 输入 / 输出

**输入**：窗口句柄；`ShellStatus(Total, Running, AwaitingApproval, Failed)`；子进程 PID。
**输出**：三个用户意图事件（显示窗口 / 新建会话 / 退出）；诊断字符串。

## 约束

- 所有调用必须在 UI 线程（窗口消息驱动）。
- 不得在窗口激活时抢先初始化任务栏——那时任务栏按钮可能还不存在，调用会静默失败。
- 图标颜色取自系统强调色，不写死色值。
- 外壳模块不得引用 Core/App 的类型；聚合逻辑留在 App 的 ViewModel 里。

## 边界条件

- 资源管理器重启：`ITaskbarList3` 调用返回 `COMException`，只降级；下次状态变化再试。
- 无任务栏（远程/精简环境）：`Initialize` 失败，托盘仍可用。
- 托盘图标创建失败：不阻塞应用启动。
- 应用崩溃（不走 `Closed`）：Job Object 随进程句柄关闭而杀子进程。

## 验收标准

- [x] 待审批时会话聚合让任务栏进入 `Paused` 且徽标为 1（实测诊断：`progress-ok=True badge=1 overlay-ok=True`）。
- [x] 继续运行后回到 `Indeterminate` 且徽标清除。
- [x] 托盘图标创建成功（`tray-visible=True`）。
- [x] 后端进程成功加入作业（`job-assign ok=True`）；强杀应用后无残留 `claude` 进程。
- [ ] 资源管理器重启后自动恢复任务栏状态（未构造用例）。
- [ ] 跳转列表（未实现）。

## 完成定义

任务栏与托盘能如实反映聚合状态，且应用无论正常退出还是被强杀都不留下后端进程。
