# PRD — Universal Skin Pipeline

> **为什么做，做什么。** 产品行为与价值，不涉及代码实现细节（代码看 ARCHITECTURE / specs）。

## 产品目标

开发一套**通用皮肤引擎管线**：将任意原始图片素材 → 按规则取色 → 生成设计 tokens → 应用到任何 GUI Agent。

管线之上的第一层应用是「**深海女仆工坊**」主题皮肤，覆盖三个桌面端目标：

| 目标 | 皮肤效果 |
|---|---|
| **QwenWork Desktop**（QwenWorkCN） | 深海蓝调替代默认绿调；宫殿背景（昼/夜随主题切换）；香子兰与同伴立绘贴底左右两侧 |
| **OpenCode Desktop** | 深海蓝调 + 暖金色调；宫殿背景（昼/夜）；角色立绘（86% / 78% 高度）；侧栏 / 主区 / 输入区透明层 |
| **Codex Desktop** | 深海蓝调配色（官方 chrome 配置 + CDP 运行时注入）；宫殿背景（昼/夜）；角色立绘贴底 |

## 核心问题

### 管线层（为什么做引擎）

1. **每个 GUI Agent 都要手写皮肤**：重复劳动，无法复用
2. **取色靠人工**：设计师手动挑色，无法自动化
3. **token 命名空间不统一**：每个 app 一套命名，皮肤移植成本高

### 应用层（为什么做皮肤）

1. **目标 app 没有官方插件机制**：想换肤只能动 `app.asar`
2. **Electron 默认 CSP 不允许外部 URL**：`url(https://...)` 和 `url(file://...)` 都被拒
3. **app 升级会覆盖 `app.asar`**：每次升级都要重打补丁
4. **React 会重渲染 `<html>` 节点上的属性**：注入一次的 `data-skin` 会被抹掉
5. **macOS / Windows / Linux 路径与进程管理不同**：三套都要覆盖

## 用户和使用场景

- **皮肤开发者**：想给某个 GUI Agent（QwenWork / OpenCode / Claude Code / Cursor 等）打一套主题
- **工作流**：拿到一组图片 → 跑 `build-tokens` → 写 token-mapping → `apply:<target>`
- **预期**：5 分钟内完成取色 + 看到预览，剩下的时间写映射 CSS

**唯一用户**：主人（项目作者本人）。这是一个**个人皮肤工程**，不是要发布给大众的「插件市场产品」，因此可以接受：用户会用终端 / 能跑 `pnpm`、不需要 GUI 安装器、不需要自动更新、不需要多语言。

## 功能及其意义

### 1. 通用取色管线（skin-core）

- **做什么**：读图片 → k-means++ 聚类（确定性，可复现）→ 按 L* 亮度分层 → 输出 light/dark 双套 46 token
- **为什么**：让取色算法和具体 app 解耦，换 target 不用重写取色逻辑；双套方案让各主题直接取用，不需要自己在 mapping 层推导暗色变体

### 2. 固定 token schema

- **做什么**：46 个核心 token（neutral / brand / semantic / text / surface / border / input / accent 八大类），light/dark 各一套完整方案
- **为什么**：统一命名空间，skin 包只写「映射」不写「取色」

### 3. 共享图片库（skin-assets）

- **做什么**：所有 target 共用同一组素材。素材库下可容纳**多个主题**（`<name>.theme/` 目录），每个主题一份 `manifest.json` 声明 4 个标准角色 + `colorSource` 取色来源；**主题只在 build-tokens 选择**（`--theme` / 交互式），preview / apply 跟随活动主题
- **为什么**：避免图片重复存储，换 target 只需换 token-mapping；多主题并存让换肤只切 `--theme` 不动文件结构；显式声明取色来源，避免场景背景污染主题色调

### 4. 各 skin 包实现

- **做什么**：token-mapping（tokens → 目标 CSS 变量）+ patch-asar（注入引擎）+ skin.css（组件样式）
- **为什么**：每个 app 的 DOM 结构 / CSS 命名不同，这层负责适配

### 5. 皮肤应用体验（qwenwork / opencode 共同）

| 功能 | 存在的意义 |
|---|---|
| CSS token 替换（light / dark 双套） | 覆盖品牌色、背景、文字、边框、状态色、阴影、中性色、控件色 |
| 宫殿背景注入（昼 / 夜两张，24% 透明度） | 营造氛围但不影响可读性 |
| 角色立绘注入（左右两张，86% / 78% 高度） | 贴底展示，不遮挡文本 |
| `data-skin` 属性保活（MutationObserver） | React 重渲染后立刻补回 |
| 幂等打补丁（marker + force） | 二次执行不重复注入；`--force` 时从 pristine snapshot 还原再打 |
| 自动备份 + 一键还原（`.skin.bak`） | 覆盖回即可完全还原 |
| 自动关闭 / 重启目标 app | 不让用户手动操作；失败不阻塞主流程 |
| 跨平台（win32 / darwin / linux） | 默认路径 + 可执行文件路径都声明，未支持的平台明确报错 |

### 6. QwenWork 专属

- 主题变体覆盖：light/dark 及 glass / classic / parchment 变体
- **跟随系统模式**：QwenWork 的「跟随系统」不设置 `data-theme` 属性，通过 `prefers-color-scheme` 媒体查询正确切换
- 品牌替换：隐藏部分原生入口（问题反馈、用量、新任务按钮等），侧边栏品牌区改标「ClaudeWork」

### 7. OpenCode 专属

- legacy + v2 两层变量覆盖（OpenCode 变量体系新旧并存）
- 弹窗（model picker / 设置等 `[role="dialog"]`）内不渲染装饰层
- favicon 替换（`icon.svg` → `favicon-v3.svg`）

## 功能关系

```
skin-assets (<name>.theme/ 多主题，每主题 manifest)
    ↓ colorSource 取色来源（--theme 选中主题）
skin-core (取色 + light/dark 双套 tokens)
    ↓ tokens.json
skins/<target> (token-mapping + skin.css + patch-asar)
    ↓ bootstrap 内联注入
app.asar (data URI + inject.js)
```

## 产品范围

- ✅ 桌面端 Electron app（QwenWork / OpenCode）
- ✅ k-means 取色算法 + light/dark 双套 token 生成
- ✅ 固定 46 token schema
- ✅ 自动备份 / 还原 / 幂等重打
- ❌ 浏览器插件（不做）
- ❌ 多套皮肤热切换（MVP 不做）
- ❌ 自动 watch 模式（MVP 不做）
- ❌ 主题切换 UI（装上就是女仆，卸了就是原版）
- ❌ 自动检测上游更新并重 patch（升级后用户手动跑一次 apply）

## Non-Goals

- 不修改目标 app 业务逻辑（消息、事件、模型请求、API 调用）
- 不引入远程资源 / 自动更新（离线可运行）
- 不替换系统二进制资源（任务栏图标、系统通知图标）
- 不自动检测 app 更新
- 不加动画 / 音效 / 用户可配置面板
- 不支持移动端 / Web 端
- 不发布到 npm / GitHub public

## 成功指标

- 安装成功率 100%（一键命令无手动干预）
- 补丁后 app 启动无异常、无性能可感知下降
- 还原操作（恢复 `.bak`）后界面完全恢复原生
- 亮 / 暗切换流畅（qwenwork 含跟随系统模式）
- light / dark 各渠道对比度检查全绿（≥4.5:1 正文 / ≥3:1 大文本与 UI 组件）

## 许可约束

所有素材为衍生创作，以 **CC BY-NC-SA 4.0** 发布，禁止商业性使用。署名链：上善（一创）→ ZipZipPipe（二创）→ Small-tailqwq（三创皮肤工程）→ wpy030414（OpenCode / QwenWork 支持）。
