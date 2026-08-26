# Spec — qwenwork token 映射（token-mapping.ts）

**对应模块**：`packages/skins/qwenwork/src/token-mapping.ts`

## 要实现什么

把 skin-core 的 46 个通用 tokens（light/dark 双套）映射到 QwenWorkCN 的 CSS 变量系统，生成 `dist/token-mapping.css`。

## 行为应该是什么

运行 `pnpm apply:qwenwork`（内部调用 `generateTokenMappingCSS()`）时：

1. 读取 `../../skin-core/dist/tokens.json`（不存在报错，提示先跑 build-tokens）
2. 生成三段 CSS：
   - **共享色阶块**：`html[data-skin]`——brand 12 级 + neutral 12 级（两套主题相同，从 light 集推导）
   - **light 语义块**：显式 light 主题选择器（light / light-glass / classic-light / light-parchment）
   - **dark 语义块**：显式 dark 主题选择器（dark / dark-glass / classic-dark / dark-parchment）
   - **跟随系统块**：`html[data-skin]:not([data-theme])` 无 data-theme 时用 `@media (prefers-color-scheme: light/dark)` 各包一套
3. 写入 `dist/token-mapping.css`（头部注释声明自动生成，勿手改）

## 映射结构

```
tokens.json { light, dark }
  ↓
buildScaleRules(): 共享色阶（24 条）
  brand: generateScale(brand-600, 明度曲线 [0.96→0.11] × 12 级 '0'..'100')
  neutral: generateScale(neutral-500, 明度曲线 [0.98→0.18] × 12 级)
  ↓
buildSemanticRules(t, mode): 语义（light/dark 各 ~110 条）
  直接消费对应主题的 token 集（S(key) = mode 对应 set 的 key）
```

**语义分档**（light/dark 差异，保证对比度）：
- `brandLink`：light `brand-600` / dark `brand-500`（链接文字 ≥4.5:1）
- `brandLinkHover`：light `brand-700` / dark `brand-300`
- `brandButton`：light `brand-600` / dark `brand-700`（按钮白字 ≥4.5:1，white on brand-600 只有 3.73:1）
- **inverted 文字恒浅**：`--text-inverted-*` 恒定 `t.light['text-inverse']`，不随主题翻转

**覆盖分组**：

| 组 | 变量前缀 | 示例 |
|---|---|---|
| Status | `--status-*` | success/warning/error/link + fill/text 变体 |
| Background | `--bg-*` | base/sidebar/card-z0/z1/pop/tooltips/page-mask |
| Text | `--text-*` | base-primary/secondary/tertiary/disable/inverted/accent |
| Border | `--border-*` | theme/light/medium/strong/focus/accent/checkbox/divider/shadow |
| Color fill | `--color-fill-*` | 5 级 + disable |
| Control | `--control-*` | core-button/ghost-button/active/input/segmented/switch/checkbox |
| Overlay | `--overlay-*` | on-container / on-primary-black × hover/pressed/selected |
| Accent | `--color-accent` | |

**COMPONENT_PALETTE**（渠道专属组件表，light/dark 手调值，不派生自图片 tokens）：
- 毛玻璃材质：`--bg-sidebar-material`（#E5EDF9d9 等 8 位带 alpha）
- 阴影色阶：`--color-shadow-2xs` ~ scrim（10 级）
- 品牌卡片色 / inverted 卡片
- 渠道色系：`--theme-blue-*` / green / red / yellow / purple / orange / cyan / magenta（50/60/70）

## 输入 / 输出

- 输入：`dist/tokens.json`（light/dark 双套）
- 输出：`dist/token-mapping.css`（**236 条映射**：共享色阶 24 + light/dark 语义各 106，控制台打印条数）

## 约束

- 从 `@skins/core` 读 tokens，**禁止硬编码颜色**（组件表除外——那是渠道专属值）
- token-mapping.ts 只写映射关系，不做取色
- CSS 中用 `var(--...)` 引用，skin.css 不重复定义颜色
- 核心 token 输出 6 位不透明 hex；带 alpha 仅限 COMPONENT_PALETTE

## 边界条件

1. **tokens.json 缺失**：报错退出（提示先 build-tokens）
2. **主题变体**：glass / classic / parchment 变体归入对应 light/dark 选择器组
3. **跟随系统**：无 data-theme 时不默认 light——用 prefers-color-scheme 正确跟随 OS（否则 OS 暗色时皮肤是「light 皮肤 + 暗色窗口」）
4. **token 缺 key**：`S(key)` 返回 undefined，生成的 CSS 值为 `undefined`——应在上游用 validateTokens 拦截

## 验收标准

- [x] 生成 CSS 三段 + 两条媒体查询，选择器全部 scoped 在 `html[data-skin]` 下
- [x] light / dark / 跟随系统三种模式配色正确（28 项对比度全绿）
- [x] 换 tokens.json 重新 apply 后皮肤颜色整体跟着变（不硬编码）
- [x] 控制台输出映射条数（236）

## 如何判断任务已经完成

`pnpm apply:qwenwork` 内部生成 token-mapping.css；apply 后 QwenWork 三模式（亮/暗/跟随系统）视觉一致达标。
