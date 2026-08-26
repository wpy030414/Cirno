# ARCHITECTURE

**系统整体如何组织。** 稳定结构，不记录每一次代码修改。

## 系统整体结构

```
┌──────────────────────────────────────────────────────────────┐
│ monorepo (personalized-agent)                                │
│                                                              │
│  packages/                                                   │
│  ├── skin-core/       通用管线（取色 + token 生成 + 共享注入）    │
│  ├── skin-assets/     共享图片库（多主题，每个 <name>.theme/ 一个 manifest）            │
│  └── skins/                                                   │
│      ├── qwenwork/    QwenWorkCN 实现（二进制补丁）             │
│      └── opencode/    OpenCode 实现（extract/repack）          │
└──────────────────────────────────────────────────────────────┘
```

## 三层架构

### Layer 1: skin-core（通用管线 + 共享注入逻辑）

**职责**：取色 + token 生成 + 共享的 bootstrap / 保活逻辑

**边界**：不碰具体 app、不写目标 CSS、不写 token-mapping

**输入**：图片路径列表（默认取**选中主题** manifest 的 `colorSource`；主题由 `--theme` / `SKIN_THEME` / TTY 交互选择）

**输出**：
- `tokens.json`：**46 个通用 design tokens × light/dark 双套**（`{ light: {...}, dark: {...} }`）
- `dist/palette.html`：可视化预览
- `buildBootstrap()` / `buildImageInjectionScript()`：共享 HTML 注入片段生成
- `inject.js`：共享的 `data-skin` 属性保活脚本

**核心模块**：
- `extract-colors.ts`：k-means++ 聚类（默认 16 簇，**确定性** mulberry32 种子 PRNG）
- `generate-tokens.ts`：颜色簇 → light/dark 双套 46 tokens（HSL 色彩空间）
- `assets-loader.ts`：多主题发现与选择（`listThemes` / `selectTheme`）+ 加载选中主题的 manifest.json（roles + colorSource）
- `bootstrap-builder.ts`：生成内联 CSS + data URI 图片 + inject.js 的 HTML 片段
- `inject.js`：共享的 `data-skin="active"` 属性保活脚本
- `palette-preview.ts`：HTML 预览生成
- `index.ts`：`buildTokens()` API + CLI 入口

### Layer 2: skin-assets（共享资产）

**职责**：存储所有 target 共用的图片。素材库下可容纳**多个主题**，每个 `<name>.theme/` 目录是一个独立主题，拥有自己的 manifest.json（标准角色映射 + colorSource 取色声明）

**边界**：纯静态资源，不碰代码

**目录结构**：
```
packages/skin-assets/
├── nekopara.theme/              ← 主题名 = 目录去 .theme 后缀
│   ├── manifest.json
│   └── *.png
└── maid-atelier.theme/
    ├── manifest.json
    └── *.webp
```

**manifest.json 结构**（schema: `standard-roles-v1`，每个主题一份）：

```json
{
  "roles": {
    "background-day": "<file>",
    "background-night": "<file>",
    "character-left": "<file>",
    "character-right": "<file>"
  },
  "colorSource": ["character-left", "character-right"],
  "char-config": {
    "character-left": { "offset": ["0%", "0%"], "height": "86%" },
    "character-right": { "offset": ["-30px", "-20%"], "height": "80vh" }
  }
}
```

**标准角色**（固定 4 个，管线消费）：
- `background-day`：背景·昼
- `background-night`：背景·夜
- `character-left`：立绘·左
- `character-right`：立绘·右

**colorSource（取色来源）**：显式声明哪些图喂给取色管线。条目可以是**角色 key**（旧契约）或**主题目录内文件名**（新用法），两种写法等价。**只从角色立绘采样，场景背景不参与取色**——宫殿昼夜图会污染主题色调（蓝色调会覆盖整个主题）。变更取色来源改 manifest，不改代码。

**char-config（角色展示配置，可选）**：键为角色 key，每角色可配 `offset`（[x, y] **CSS 值字符串**：x = 距边缘——左角色距左、右角色距右；y = 距底，默认 `["0%", "0%"]`，可为负——负值让立绘探出视窗，如半身像）与 `height`（**CSS 值字符串**，默认 `"86%"`，如 `"80vh"`）。build-tokens 读入并写入 tokens.json，apply 时据此生成 `--character-*-height` / `--character-*-position` CSS 变量（offset 值原样透传进 `calc(100% - <value>)`），skin.css 的 `background-size` / `background-position` 以 `var()` 消费——**调角色位置/高度只改 manifest，不动 CSS**。

**主题选择**：**只有 build-tokens 选择主题**——`selectTheme()` 交互式选择（TTY 编号菜单）或报错列出可用主题（非 TTY），指定方式为 `--theme <name>` 参数或 `SKIN_THEME` 环境变量。选定主题写入 `dist/tokens.json` 顶层 `theme` 字段，**preview / apply 等其他模块不选择主题**，一律经 `getActiveTheme()` 跟随该字段。

### Layer 3: skins/<target>（具体实现）

**职责**：消费 core 的 tokens，映射到目标 app 的 CSS 变量系统，并负责打补丁注入

**边界**：不改 core 的 schema，只写 token-mapping、skin.css、patch-asar

**每个 target 包含**：
- `token-mapping.ts`：读取 `dist/tokens.json`（light/dark 双套），生成 `dist/token-mapping.css`（目标 app 的 CSS 变量）。分三段输出：共享色阶块 + light 语义块 + dark 语义块
- `skin.css`：手写的组件样式（布局、透明度、选择器）
- `patch-asar.ts`：打补丁引擎（策略因 target 而异）
- `index.ts`：CLI 入口（解析 `--no-force` / `--no-backup` / `--allow-running`；主题不在此选择，apply 跟随活动主题）

**注意**：
- `inject.js` 在 skin-core 共享，skin 包不重复维护
- 图片一律走 data URI 注入（构建时 base64 内联进 bootstrap 脚本，不依赖 `oc://` 协议或文件系统路径）
- 两个 target 的 patch 策略差异：QwenWork 二进制补丁，OpenCode extract/repack（见 DECISIONS D-004）

## Token Schema（46 个，light/dark 双套）

```
neutral (12 级): neutral-50 ~ neutral-1100      中性色阶（light→dark）
brand   (6 级):  brand-100/300/500/600/700/900  品牌主色阶
semantic (12 个): success/warning/critical/info × weak/base/strong
text    (5 个):  text-strong/base/weak/weaker/inverse
surface (4 个):  surface-base/raised/strong/weak
border  (4 个):  border-base/weak/strong/focus
input   (2 个):  input-base/active
accent  (1 个):  accent
```

生成策略（`generate-tokens.ts`）：

```
16 个聚类
  ↓ 按饱和度分类（< 0.25 中性 / ≥ 0.25 彩色）
  ├── 中性簇 → 固定明度范围 [0.97 → 0.06] 插值 12 级 neutral
  │           （色相取最亮中性簇，饱和度 clamp 至 0.08——角色图常带肤色/发色，
  │             不 clamp 会把中性面染成粉/米色）
  ├── 彩色簇 → 最饱和者作 brand 种子，生成 6 级 brand 色阶
  │           （light: 明度 0.88→0.15；dark: 0.95→0.32——保证对比度 ≥4.5:1）
  ├── 语义色 → 按色相匹配绿/黄/红/蓝 → success/warning/critical/info
  │           （弱饱和或近黑近白的簇不可用；未命中用 fallback 固定色）
  └── 点缀色 → 与 brand 色相差 > 0.1 的第二个彩色簇 → accent
  ↓
light / dark 两套：
  light: 表面在浅端、文字在深端、语义 deep
  dark:  表面在深端、文字在浅端、语义 brightened
```

约束：
- 簇数 < 8 抛错；中性簇 < 2 或彩色簇 < 2 抛错
- **所有 token 输出 6 位不透明 hex**（透明度通道只允许出现在 skin 包的 COMPONENT_PALETTE 组件表中）
- 确定性：相同输入 → 相同输出（PRNG 种子从像素数据推导）

## 数据流

### 构建时

```
1. pnpm build-tokens --theme <name>（未指定则 TTY 交互选择 / 非 TTY 报错）——主题选择只在这里发生
   skin-core: selectTheme() → assets-loader 读选中主题 manifest.colorSource → 立绘图
     ↓
   extract-colors.ts: sharp 解码 → 100x100 resize → 每 2 像素采样 → k-means++ (k=16)
     ↓
   generate-tokens.ts: 簇 → { light, dark } 双套 46 tokens
     ↓
   dist/tokens.json（顶层 theme 字段 = 活动主题）
     ↓
2. pnpm apply:<target>（不选主题——getActiveTheme() 跟随 tokens.json 的活动主题）
   skins/<target>/index.ts:
     a. 读 tokens.json → generateTokenMappingCSS() → 写 dist/token-mapping.css
     b. patch-asar: token-mapping.css + skin.css → buildBootstrap()
        + 活动主题的 4 张立绘 → data URI → buildImageInjectionScript()
        + inject.js
        → 注入到目标 app 的 out/renderer/index.html（</head> 之前）
        → 重打包 / 二进制补丁 → 校验 marker → 安装 → 重启
```

### 运行时

```
目标 app 启动
  ↓
加载 out/renderer/index.html
  ↓
bootstrap <script> 跑：
  1. document.documentElement.dataset.skin = "active"
  2. setTimeout(1ms) 把 4 张立绘以 url(data:...) 写入 CSS 变量（延迟防 OOM watchdog）
  3. inject.js 启动 MutationObserver 保活 data-skin
  ↓
CSS 选择器 html[data-skin] 命中
  ↓
皮肤可见
```

## 核心模块

### 1. skin-core/src/extract-colors.ts

**职责**：从图片提取主色

**关键设计**：
- k-means++ 初始化 + **确定性 PRNG（mulberry32，种子从像素前 64 像素推导）**——相同输入永远产出相同聚类，tokens 可复现
- 100x100 resize（性能优化）
- 每 2 像素采样（速度）
- 按 L*（CIE LAB 亮度）排序输出（darkest first）
- 多图流程：各图先聚 16 簇，再对全部簇做二次 k-means 合并

### 2. skin-core/src/generate-tokens.ts

**职责**：颜色簇 → 46 token light/dark 双套

**关键设计**：
- 中性/彩色按饱和度分类（阈值 0.25；中性不足 2 个时借最不饱和的彩色簇）
- 中性色阶**固定明度范围** [0.97 → 0.06]（不用图片实际范围——否则色阶被挤压在中段，无真白/真黑，正文对比度失败）；饱和度 clamp 至 0.08
- brand 色阶 light/dark 用不同明度范围，保证链接/强调文字 ≥4.5:1
- 语义色按色相匹配 + 明度/饱和度可用性过滤，未命中用 fallback（light/dark 各自调优）
- text/surface/border/input 从 neutral 阶取对应级别，light/dark 取相反端
- 导出工具函数（rgbToHex / hexToRgb / rgbToHsl / hslToRgb / adjustLightness / lerpRgb / generateScale）供 skin 包自行推导色阶

### 3. skin-core/src/assets-loader.ts

**职责**：多主题发现与选择 + 加载选中主题的 manifest.json

**关键设计**：
- `listThemes()`：扫描 `skin-assets/*.theme/` 目录（含 manifest.json），返回排序后的主题名
- `selectTheme(themeArg?)`：**仅 build-tokens 调用**——`--theme` 参数 > `SKIN_THEME` 环境变量 > TTY 交互编号菜单 > 非 TTY 报错；0 个主题直接报错
- `getActiveTheme()`：**其他模块跟随入口**——读 `dist/tokens.json` 顶层 `theme` 字段并校验主题存在；缺失时报错提示先跑 build-tokens
- `getRoleImagePath(role, theme)` / `getAllRoleImagePaths(theme)`：4 个标准角色的物理路径
- `getColorSourceImagePaths(theme)`：仅返回 `colorSource` 声明的图（取色专用）；条目支持角色 key 或文件名两种写法
- **主题选择唯一入口是 build-tokens**，其他模块一律跟随活动主题
- manifest 缺少非空 `colorSource` 数组或 4 个角色任一缺失时报错
- 解耦主题文件名和标准角色名

### 4. skin-core/src/bootstrap-builder.ts

**职责**：生成共享的 HTML 注入片段

**关键设计**：
- `buildBootstrap({ css, injectJs, marker })`：统一生成 `<style id="{marker}-style">` + `<script id="{marker}-script">` + marker 注释块
- `buildImageInjectionScript()`：经 `getActiveTheme()` 读取活动主题 manifest 的 4 个角色 → base64 data URI → 生成延迟注入脚本（主题不在此选择）
- `imageFileToOptimizedDataUri()`：**大图压缩**——data URI > 1.5MB 的图用 sharp 缩放（上限 1920×1080）+ 转 webp（q82）；小图原样。原因：Chromium `kMaxURLChars` = 2MB，超长 URL 静默失效，背景图会直接消失（见 DECISIONS D-024）
- `imageFileToDataUri()`：文件 → data URI（不含 url() 包装）
- `injectBootstrapIntoHtml(html, bootstrap)`：在 `</head>` 前注入
- **setTimeout(1ms) 延迟加载**：避免一次性注入大图触发 Electron OOM watchdog

**输出格式**：
```html
<!-- {marker} start -->
<style id="{marker}-style">
  /* token-mapping.css + skin.css 合并 */
</style>
<script id="{marker}-script">
  ;(function(){ document.documentElement.dataset.skin = "active" })();
  ;(function(){ /* 4 个 CSS 变量 = url(data:...) 延迟注入 */ })();
  /* inject.js 保活逻辑 */
</script>
<!-- {marker} end -->
```

### 5. skin-core/src/inject.js

**职责**：DOM 属性保活

**关键设计**：
- IIFE + 'use strict'，不污染全局
- MutationObserver 监听 `<html>` 的 `data-skin` 属性（`attributeFilter: ['data-skin']`）
- 被抹掉时立刻补回 `"active"`
- `document.readyState === 'loading'` 时等 `DOMContentLoaded` 再启动
- 所有 skin 包共享同一份脚本

### 6. skins/<target>/token-mapping.ts

**职责**：通用 tokens → 目标 CSS variables

**关键设计**：
- 由各 skin 包作者维护（不是 skin-core）
- 读取 `../../skin-core/dist/tokens.json`（light/dark 双套）
- 生成 `dist/token-mapping.css`，三段结构：
  1. `html[data-skin]` 共享色阶块（brand/neutral 等，两套主题相同，从 light 集推导）
  2. light 语义块（如 qwenwork `data-theme="light"` 系列 / opencode `data-color-scheme="light"`）
  3. dark 语义块
- 使用**函数映射**（`{ target, source: (t) => string }`），支持计算与 fallback
- **COMPONENT_PALETTE**：渠道专属组件表（light/dark 两套手调值，不派生自图片 tokens）——透明/毛玻璃材质、阴影、渠道特有语义色（syntax/markdown/diff/avatar 等）
- 语义分档：链接用亮档 brand、按钮用深档 brand（保证白字 ≥4.5:1）；inverted 文字恒定浅色不随主题翻转

**示例（qwenwork）**：
```typescript
{ target: '--brand-brand-500', source: () => brand[5] },
{ target: '--status-success',   source: () => S('success-base') },
{ target: '--text-accent-primary', source: () => brandLink },   // light: brand-600, dark: brand-500
{ target: '--control-core-button-default', source: () => brandButton }, // light: brand-600, dark: brand-700
// ... 共 236 条（共享色阶 24 + light/dark 语义各 106）
```

**示例（opencode）**：
```typescript
{ target: '--v2-grey-500', source: () => grey[4] },
{ target: '--v2-blue-600', source: () => blue[5] },
{ target: '--v2-text-text-inverse', source: () => `var(--v2-grey-50)` }, // 恒定浅色
// ... 共 491 条（共享色阶 109 + light/dark 语义各 191）
```

**主题选择器差异**：

| | QwenWork | OpenCode |
|---|---|---|
| 主题属性 | `data-theme`（light/dark/glass/classic/parchment 变体） | `data-color-scheme`（light/dark） |
| 跟随系统 | **无 data-theme 属性**，用 `@media (prefers-color-scheme)` 包裹两套 | 无此模式 |

### 7. skins/<target>/skin.css

**职责**：组件样式（布局、透明度、选择器等）

**关键设计**：
- 手写，不依赖自动生成的 tokens（颜色全部走 token-mapping.css 定义的 CSS 变量）
- 所有选择器 scoped 在 `html[data-skin]` 下——删掉属性即完全还原
- 主内容卡片装饰：`::before` 放宫殿背景（opacity 0.24，昼/夜分支），`::after` 放角色立绘（`background-image: var(--character-left), var(--character-right)`，位置与高度由 `--character-*-position` / `--character-*-height` 变量控制，默认贴边贴底、高 86%），子元素 `z-index: 2` 保证内容在装饰层之上
- 装饰层排除弹窗（qwenwork 排除右侧辅助面板；opencode 排除 `[role="dialog"]` 内元素）
- 历史教训：**背景必须不透明**——此前 `transparent` / `color-mix` 半透明让聊天文字直接叠在装饰层上，可读性灾难（详见 DECISIONS D-020）

### 8. skins/qwenwork/patch-asar.ts（二进制补丁）

**职责**：直接对 asar 文件做二进制补丁，只改 `out/renderer/index.html`

**为什么**：QwenWorkCN 的 unpacked 原生模块（sharp / node-pty 等）在 ARM Mac 上 extractAll 会失败

**关键设计**：
- 手工解析 Chromium Pickle 格式 header（`readAsarHeader` / `buildAsarHeaderBuf`）
- 只改 HTML 条目的 size + 后续文件 offset，header JSON 用尾部空格 padding 保持**等长**，其余条目（含 unpacked 标记）原样保留
- 注入点：`</head>` 之前（CSS 变量在 React bundle 跑之前就位，避免 FOUC）
- **OOM watchdog 等长替换**：`oomWatchdogService.start()` → `0/*disabled-watchdog-pad*/`（同长度，不影响 offset），避免注入大图后 app 自杀
- **integrity hash 更新**：sha256 重算写入 header（chromium integrity 校验）
- **macOS 重新签名**：`codesign --force --deep --sign -`
- 旧 marker（`qwenwork-maid-atelier`）残留 bootstrap 块自动剥离
- 校验：marker 存在 + unpacked 条目数不变
- 流程：备份 → 解析 header → 读 HTML → 注入 bootstrap → 更新 header → 写临时 asar → 校验 → 覆盖安装 → 重启

### 9. skins/opencode/patch-asar.ts（extract/repack）

**职责**：标准 `@electron/asar` API 提取 → 改写 → 重打包

**为什么**：OpenCode 没有 unpacked 原生模块问题，标准 API 更简单可靠

**关键设计**：
- `asar.extractAll()` → 改 `out/renderer/index.html`（注入 bootstrap 到 `</head>` 前）→ `asar.createPackage()` 重打包
- `--force` 时从 `.bak` 提取 pristine HTML 还原再打，并校验备份未被污染
- 改写前校验 HTML 包含 `oc-theme-preload-script` 字符串（防止对错误的文件打补丁）
- favicon 替换：`src/icon.svg` → `out/renderer/favicon-v3.svg`（存在才替换）
- 校验 repacked asar 含 marker 才安装
- 临时目录 `os.tmpdir()/opencode-skin-patch-*`，finally 强制清理
- 流程：备份 → extract → 注入 → 替换 favicon → repack → 校验 → 安装 → 重启

### 10. skins/codex（官方 chrome 配置 + CDP 运行时注入）

**职责**：Codex Desktop（MSIX，受保护目录）的换肤——不碰 app.asar。

**为什么**：WindowsApps 只读且直启 exe 被应用容器拒绝，asar 内无 HTML 锚点（DECISIONS D-025）。

**关键设计**：
- 主通道：COM `IApplicationActivationManager` 激活传参（--remote-debugging-port）→ CDP WebSocket 注入（`--color-token-*` 变量映射 + 立绘 data URI + 保活）
- 辅通道：config.toml `[desktop.appearance*ChromeTheme]` 颜色键（行级段编辑，段外字节零改动）
- 交付物：dist/token-mapping.css（CDP 层）+ token-mapping.toml（官方层）+ codex-theme-v1 导入串（云端持久配色）
- 注入共享：复用 skin-core 的 `buildBootstrapBody()`（data-skin setter + 立绘注入 + inject.js 保活），不重复实现

## 外部系统

| 外部系统 | 关系 |
|---|---|
| QwenWorkCN Desktop | target，被 patch 的对象（二进制补丁） |
| OpenCode Desktop | target，被 patch 的对象（extract/repack） |
| Codex Desktop（MSIX） | target：官方 chrome 配置（config.toml 外观键）+ CDP 运行时注入（不碰受保护的 app.asar） |
| 各 app 升级服务 | 会覆盖 `app.asar`，需用户手动重 patch |
| macOS / Windows / Linux 文件系统 | `app.asar` 路径与进程管理各不同 |
| Electron 默认 CSP | 阻止外部 URL，故用 data: URI 内联 |
| Electron OOM watchdog | 注入大图可能触发，需等长禁用（qwenwork） |
| macOS codesign | 修改 .app 后需重新签名（qwenwork） |

## 重要技术边界

1. **skin-core 不碰具体 app**：只输出 tokens 和共享逻辑，不写 patch-asar、不写 CSS
2. **skin-assets 不碰代码**：纯静态资源 + 每主题一份 manifest.json（roles + colorSource）；主题名 = `<name>.theme/` 目录名
3. **skins/<target> 不改 core schema**：只写 token-mapping、skin.css、patch-asar
4. **所有 CSS 选择器 scoped 在 `html[data-skin="active"]`**：删除属性 = 完全还原
5. **图片走 data: URI**：不依赖网络、不依赖文件系统路径（CSP 拒绝 `file://`）
6. **token-mapping.ts 由 skin 包维护**：skin-core 不定义目标 app 的变量名
7. **inject.js 全局共享**：所有 skin 包复用同一份保活逻辑
8. **bootstrap 生成逻辑统一**：所有 skin 包复用 `buildBootstrap()` + `buildImageInjectionScript()`
9. **取色只采 colorSource 角色**：场景背景不得影响主题色调
10. **token 输出不透明**：核心管线 6 位 hex；带 alpha 仅限各 skin 的 COMPONENT_PALETTE

## 构建命令

| 命令 | 作用 |
|---|---|
| `pnpm build-tokens --theme <name>` | 从选中主题的 colorSource 图提取颜色，生成 light+dark 双套 46 tokens（未指定主题则交互选择；主题唯一选择点） |
| `pnpm preview` | 生成 palette.html 可视化预览（簇 + light/dark 双套 token） |
| `pnpm apply:qwenwork` | 生成 token-mapping.css + 给 QwenWorkCN Desktop 打补丁（二进制） |
| `pnpm apply:opencode` | 生成 token-mapping.css + 给 OpenCode Desktop 打补丁（extract/repack） |
| `pnpm apply:codex` | 生成 dist 产物（CSS + TOML + 导入串）+ 给 Codex Desktop 换肤（config 通道 + CDP 注入） |
| 各 skin 包内 `pnpm dev` | kill + 带 DevTools/CDP 重启（dev 脚本在子项目 package.json 里，不在顶层） |

## 典型工作流

```
1. 新增主题：建 packages/skin-assets/<name>.theme/ 目录 → 放图片 → 写 manifest.json
   （改主题图/取色来源也只改 manifest，不动代码）
   ↓
2. pnpm build-tokens --theme <name>   ← 主题选择只在这里发生
   → skin-core 提取 light/dark 双套 46 tokens，theme 字段写入 tokens.json
   ↓
3. pnpm preview                        ← 跟随活动主题，不选主题
   → 浏览器检查取色结果（可选）
   ↓
4. 调整 skin.css（如果需要）
   → 手写组件样式
   ↓
5. pnpm apply:<target>                 ← 跟随活动主题，不选主题；内部自动生成 token-mapping
   → patch-asar 注入到 app.asar（自动备份 + 重启）
   ↓
6. cd packages/skins/<target> && pnpm dev
   → 各 skin 包内的 dev 启动器（kill + 带 DevTools/CDP 重启）
```

切换主题 = 重跑 `pnpm build-tokens --theme <name>`（更新活动主题）→ apply。
