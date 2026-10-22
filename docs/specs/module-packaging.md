# Spec — 分发与打包（MSI）

## 要构建什么

一个双击即装的 **MSI**：目标机器不需要预装 .NET、Windows App SDK 或 VC++ 运行库。
不做：自动更新、应用商店分发、跨平台包。

## 行为

### 依赖矩阵（实测）

| 依赖 | 是否随包 | 处理方式 |
|---|---|---|
| .NET 10 运行时 | 是 | `dotnet publish --self-contained` |
| Windows App SDK 运行时 | 是 | `WindowsAppSDKSelfContained=true` |
| VC++ 2015–2022 x64 运行库 | 是 | 从编译器 redist 目录 app-local 拷贝（微软允许随应用再分发） |
| WebView2 运行时 | 不需要 | 产品不使用 WebView2 |
| `claude` CLI | 否（用户自装） | 会话启动时检测，给出安装指引，不阻塞安装 |
| Windows 11 22000+ | 否 | 低于该版本 Mica 降级为纯色，不阻断安装 |

**关键取舍**：把三件事全部做进包里，「缺依赖」从安装期问题变成不存在的问题。
代价是安装体积 227MB、MSI 77MB。

### 构建

```powershell
installer/build-msi.ps1 -Version 0.1.0
```

步骤：自包含发布 → 拷贝 VC 运行库 → 生成组件清单 → WiX 打包。

- 组件清单由 `installer/generate-components.ps1` 生成：**一个文件一个组件**，组件 Id 取相对路径的哈希，
  保证多次构建稳定（MajorUpgrade 才能正确比对）。生成文件不入库。
- 工具固定 **WiX 5.0.2**：WiX v7 起要求接受 OSMF 商业授权，v5 是最后一个可直接使用的免费版本。

### 安装

- **按用户安装**：`%LOCALAPPDATA%\Programs\Cirno`，不需要管理员权限。
- 开始菜单快捷方式，图标直接取自 `Cirno.exe`。
- 控制面板条目带图标（`ARPPRODUCTICON`）且不可修改（`ARPNOMODIFY`）。
- 主版本升级用 `MajorUpgrade`。
- 写注册表标记 `HKCU\Software\Cirno\ShutdownSupported=1`：表示已装版本支持优雅关闭开关。

### 升级时关闭运行中的实例

两条路径叠加（见 ADR-129）：

1. `util:CloseApplication`（`Target="Cirno.exe"`）：标准的进程关闭检测，给用户「重试 / 中止」提示。
   `RebootPrompt="no"` 表示关不掉就报错，而不是默默安排重启。
2. CustomAction 调用已装好的 `Cirno.exe --shutdown`：走应用自己的单实例管道，让主实例主动收尾
   （回退挂起审批、关子进程、落 WAL checkpoint）。Cirno 常驻托盘，WM_CLOSE 未必可靠。

第 2 条由注册表标记 `ShutdownSupported` 把关：旧版不认识这个参数，贸然调用会当成普通启动而弹窗。

### 卸载

- 删除安装目录、快捷方式与 ARP 条目。
- **默认保留** `%LOCALAPPDATA%\Cirno`（会话数据库）。需要彻底清除时手工删除该目录。

## 输入 / 输出

**输入**：`dotnet publish` 的自包含输出；构建机上的 VC redist；版本号（由脚本注入 `ProductVersion`）。
**输出**：`artifacts/Cirno-<version>-x64.msi`（单文件、可静默 `/qn`、可升级）。

## 约束

- MSI 内不得包含任何网络请求。
- 安装包必须可在离线机器上完成安装。
- 用户数据与安装目录严格分离，卸载不误删。
- 发布产物必须包含 `<AssemblyName>.pri`（见 ADR-123），否则装上后一启动就崩。

## 边界条件

- 无管理员权限：按用户安装，无需提权。
- 安装包未签名：SmartScreen 警告属预期，正式分发前用受信证书签名。
- 升级时应用仍在运行：`util:CloseApplication` 提示用户关闭；`--shutdown` 走管道优雅退出。
- 旧版（不支持 `--shutdown`）在跑时：注册表标记缺失，跳过管道路径，只靠 `util:CloseApplication`。
- 自定义动作失败（如主实例本来就没在跑）：`Return="ignore"`，不中断安装。

## 已知缺口

- **`REMOVEDATA=1` 未实现**。WiX 的 `RemoveFolderEx` 不支持「卸载时按属性条件执行」，
  纯 MSI 内实现需要自定义动作；M6 选择不做，改为文档说明手工删除用户数据。
- 未做代码签名。
- 覆盖升级未做端到端验证（MSI 构建通过、`--shutdown` 通路单独验证过，但未跑完整的「装旧版 → 运行 → 装新版」流程）。

## 验收标准

- [x] 安装成功：540 个文件、227MB，开始菜单快捷方式存在。
- [x] 安装后的 exe 可直接启动并稳定存活。
- [x] 卸载：安装目录与快捷方式移除，用户数据保留。
- [x] 发布产物自带 VC 运行库（`vcruntime140.dll` 等 8 个 DLL 在应用目录）。
- [x] 升级前关闭运行中实例：`--shutdown` 让主实例完全退出（进程列表中消失）。
- [ ] 在真正「无 VC++ / 无 .NET」的干净机器上安装：本机已装 VC++，只有 app-local 拷贝这一间接证据。
- [ ] 覆盖升级保留用户数据：未构造用例。
- [ ] 代码签名：未做。

## 完成定义

在从未装过开发工具的 Windows 11 机器上，双击 MSI 装完即可启动并跑通一次会话；
不需要用户自行安装任何运行时。
