# AGENTS.md

本文件定义 Agent 在本 monorepo 中工作时的边界与约束喵～

## 心智模型

### Universal Skin Pipeline

本 monorepo 是**通用皮肤引擎**，不是单项目：

- **skin-core**：通用管线（取色 + token 生成 + 共享注入逻辑），产出固定 token schema
- **skin-assets**：共享图片库，所有 target 共用
- **skins/<target>**：各 GUI Agent 的具体实现，消费 core 的 tokens

### Pipeline 数据流

```
图片 (skin-assets <name>.theme/ 选中主题，仅 colorSource 图)
  ↓ extract-colors.ts (k-means++, 确定性 PRNG)
颜色簇 (ColorCluster[], 按 L* 亮度排序)
  ↓ generate-tokens.ts
tokens.json (46 个 token × light/dark 双套)
  ↓ skins/<target>/token-mapping.ts
目标 CSS variables (dist/token-mapping.css)
  ↓ skins/<target>/patch-asar.ts 或 cdp-inject.ts（codex：运行时注入）
app.asar (data URI 图片 + inject.js 保活)
```

### Token Schema

skin-core 保证输出 **46 个固定 token**，且同时产出 **light / dark 两套完整方案**（`tokens.json` 结构为 `{ light: {...}, dark: {...} }`）：

```
neutral (12 级): neutral-50 ~ neutral-1100
brand   (6 级):  brand-100/300/500/600/700/900
semantic (12):   success/warning/critical/info × weak/base/strong
text    (5):     text-strong/base/weak/weaker/inverse
surface (4):     surface-base/raised/strong/weak
border  (4):     border-base/weak/strong/focus
input   (2):     input-base/active
accent  (1):     accent
```

每个 skin 包只写 **token-mapping.ts**（映射到目标 CSS variables），不改 core 的 schema。

### 取色来源（colorSource）

`packages/skin-assets/<name>.theme/manifest.json`（每主题一份）显式声明 `colorSource` 取色数组——**只从这些图采样取色**，条目可以是角色 key 或主题目录内文件名。场景背景（昼/夜）不允许影响主题色调。取色来源的变化需要改 manifest，而非改代码。

### 主题选择

skin-assets 可容纳多个 `<name>.theme/` 目录。**主题选择只发生在 build-tokens**（`--theme <name>` 参数 > `SKIN_THEME` 环境变量 > TTY 交互选择 > 非 TTY 报错），选定主题写入 `dist/tokens.json` 顶层 `theme` 字段。**preview / apply 等其他模块不选择主题**，一律通过 `getActiveTheme()` 跟随活动主题。禁止在其他模块引入主题选择。

## Code Review Rules

### Skin Lifecycle

- 所有 DOM/CSS 变更、Observer、事件监听、定时器、动画帧、注入节点均视为皮肤自有状态。
- 标记以下路径中的任何泄漏风险：`apply()` 部分失败、销毁、重复激活、热切换遗留状态或误删其他激活态的状态。
- 安全路径：在可失败操作**之前**注册清理回调，保留精确的原始值和自有句柄，仅还原当前激活所修改的内容。

### Product Compatibility

- 本 monorepo **仅交付展示层皮肤**。
- 标记以下变更：修改目标 app 业务逻辑、要求远程运行时资源、遮挡原生控件/叠层、依赖不稳定 DOM 选择器且无安全降级。
- 安全路径：CSS 和 DOM 装饰限定在当前激活皮肤范围内，保持原生行为在亮/暗主题、窄/宽侧栏、对话/工作区视图下均正常。

### Distribution and Attribution

- 素材为 CC BY-NC-SA 4.0 衍生创作。
- 标记以下问题：源码或素材变更但产物未同步、生成包包含绝对路径或远程依赖、素材/许可变更破坏署名链。
- 安全路径：仅从仓库输入重新生成包，署名链变更时同步更新根 `README.md` 的署名表。

## Non-Goals（绝对不做的事）

1. **不修改目标 app 业务逻辑**：不触碰消息、事件、模型请求、API 调用。
2. **不引入远程资源依赖**：所有素材以 data: URI 内联入 asar，皮肤离线可运行。
3. **不替换二进制系统资源**：任务栏图标（`*.ico`）、系统通知图标不做替换（opencode 的 favicon SVG 除外，这是页面资源）。
4. **不维护上游 app 代码**：本 monorepo 是独立分发，不是目标 app 的 fork。
5. **不自动检测上游更新**：`app.asar` 被应用更新后需用户手动重跑 `pnpm apply:<target>`。
6. **不扩展皮肤功能范围**：不加动画、不加音效、不加用户可配置面板——纯视觉主题。
7. **不支持浏览器端插件分发**：仅面向桌面端（asar 补丁或等价运行时注入）。

## 工作区约定

- **语言**：TypeScript（ES2022 + NodeNext），无编译步骤，`tsx` 直接执行。
- **包管理**：pnpm（`pnpm-workspace.yaml` 中 `allowBuilds.esbuild: true` / `sharp: true`）。
- **样式表**：各 skin 包 `src/skin.css` 为直接编辑目标（组件规则），token 颜色全部由 `token-mapping.ts` 生成，skin.css 不得硬编码颜色。
- **素材**：`packages/skin-assets/<name>.theme/`（每主题一个目录），文件名自由，经各自 `manifest.json` 登记到标准角色；角色 key 固定 4 个（background-day / background-night / character-left / character-right）。新增主题 = 新建 `<name>.theme/` 目录 + manifest，不改代码。
- **CSS 选择器**：全部挂在 `html[data-skin]` 下，禁止裸全局选择器；删除属性 = 完全还原。
- **图片加载**：一律走 data: URI 内联（构建时 base64），不依赖网络、不依赖文件系统路径、不依赖 `oc://` 等自定义协议。
- **注入共享**：bootstrap 构建（`buildBootstrap`）、图片注入脚本（`buildImageInjectionScript`）、属性保活（`inject.js`）都在 skin-core，skin 包不重复实现。
- **透明度通道**：核心 token 管线输出全部 6 位不透明 hex；带 alpha 的 8 位 hex 仅允许出现在各 skin 的 COMPONENT_PALETTE 组件表中。

## 各子包职责

| 包 | 职责 | 不改 |
|---|---|---|
| skin-core | 取色 + token 生成 + 共享注入逻辑 | 不碰具体 app、不写 CSS、不写 token-mapping |
| skin-assets | 共享图片库（多主题）+ 每主题 manifest | 不碰代码、不碰 tokens |
| skins/qwenwork | QwenWork 皮肤实现（二进制补丁） | 不改 core 的 schema |
| skins/codex | Codex Desktop 皮肤实现（官方 chrome 配置 + CDP 运行时注入） | 不改 core 的 schema |
| skins/opencode | OpenCode 皮肤实现（extract/repack） | 不改 core 的 schema |

## 验证清单

1. `pnpm install` → 无报错
2. `pnpm build-tokens --theme maid-atelier` → 输出 `dist/tokens.json`，包含 light/dark 各 46 个 token
3. `pnpm build-mapping:qwenwork` + `pnpm build-mapping:opencode` → 两个 skin 包都生成 `dist/token-mapping.css`
4. `pnpm preview` → 生成 `dist/palette.html`，浏览器打开看可视化
5. `pnpm apply:qwenwork` → QwenWorkCN 重启带皮肤，`app.asar.skin.bak` 生成
6. `pnpm dev:qwenwork` → QwenWorkCN 重启 + `Option+Cmd+I` 能开 DevTools
7. `pnpm apply:opencode` → OpenCode 重启带皮肤
8. `pnpm build-mapping:codex` → 生成 dist/token-mapping.css + token-mapping.toml + 官方导入串
9. `pnpm apply:codex` → Codex Desktop 经 COM 激活重启（--remote-debugging-port=9335）并注入皮肤（会话内生效，重启后需重跑 apply）
10. `pnpm apply:codex --pin light|dark`（仅测试）→ 明/暗切换验证；`pnpm apply:codex --restore` → 还原 config.toml 并重启原生
11. 亮/暗主题切换正常（qwenwork 含 follow-system 模式）
12. 还原：`.bak` 覆盖回 `app.asar` 后界面完全恢复原生
