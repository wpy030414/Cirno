# Spec — opencode token 映射（token-mapping.ts）

**对应模块**：`packages/skins/opencode/src/token-mapping.ts`

## 要实现什么

把 skin-core 的 46 个通用 tokens（light/dark 双套）映射到 OpenCode Desktop 的 CSS 变量系统（legacy + v2 两层），生成 `dist/token-mapping.css`。

## 行为应该是什么

运行 `pnpm apply:opencode`（内部调用 `generateTokenMappingCSS()`）时：

1. 读取 `../../skin-core/dist/tokens.json`（不存在报错，提示先跑 build-tokens）
2. 生成三段 CSS：
   - **共享色阶块**：`html[data-skin]`——9 个色系 × 13 级（grey 50 + 12 级 / 8 色相 × 12 级）
   - **light 语义块**：`html[data-skin][data-color-scheme="light"]`
   - **dark 语义块**：`html[data-skin][data-color-scheme="dark"]`
3. 写入 `dist/token-mapping.css`（头部注释声明自动生成，勿手改）

## 映射结构

```
tokens.json { light, dark }
  ↓
buildScaleRules(): 共享色阶（8×12+13 = 109 条）
  scale12(seed): LIGHTNESS_100_1200 = [0.96→0.11] × 12 级 '100'..'1200'
  grey   ← neutral-500
  blue   ← brand-600
  green  ← success-base
  yellow ← warning-base
  red    ← critical-base
  cyan   ← info-base
  purple / pink ← accent
  orange ← warning-base
  grey-50 恒为 #FFFFFF
  ↓
buildSemanticRules(t, mode): 语义（light/dark 各 ~130 条）
  直接消费对应主题的 token 集（S(key) = mode 对应 set 的 key）
```

**语义分档**（与 qwenwork 相同原则，见 DECISIONS D-018）：
- `brandLink`：light `brand-600` / dark `brand-500`
- `brandLinkHover`：light `brand-700` / dark `brand-300`
- `brandButton`：light `brand-600` / dark `brand-700`
- **inverted 恒浅**：`--v2-text-text-inverse` / `--v2-icon-icon-inverse` 恒定 `var(--v2-grey-50)`（白色）——永远坐在深色强调表面上

**覆盖分组**：

| 组 | 变量前缀 | 说明 |
|---|---|---|
| V2 背景 | `--v2-background-bg-*` | base/deep/layer-01~04/inverse/contrast/button-neutral/accent |
| V2 文本 | `--v2-text-text-*` | base/muted/faint/inverse/contrast/accent(+hover)/code-accent |
| V2 图标 | `--v2-icon-icon-*` | base/muted/inverse/contrast/accent(+hover) |
| V2 边框 | `--v2-border-border-*` | base/muted/strong/focus/inverse |
| V2 状态 | `--v2-state-{fg,bg,border}-{success,warning,danger,info}` | 语义色 fg=strong / bg=weak / border=base |
| V2 叠层 | `--v2-overlay-simple-*` | scrim/hover/pressed/tab 系列 → surface-strong |
| V2 头像 | `--v2-avatar-{bg,border}-{hue}` | 8 色相引用色阶（100/300）+ gray |
| Legacy 别名 | `--background-*` / `--surface-*` / `--text-*` / `--border-*` / `--button-*` / `--input-*` / `--icon-*` / `--syntax-*` / `--markdown-*` | 旧 skin.css 规则使用的变量 |

**COMPONENT_PALETTE**（渠道专属组件表，light/dark 手调值，不派生自图片 tokens，约 90 条）：
- 表面交互态：surface-raised-base-hover / interactive-* / weaker
- 输入态：input-hover / selected / disabled
- 边框交互态：border-hover / active / selected / interactive-* / success/warning/critical/info
- 图标：icon-weak/strong/success/warning/critical/info/on-brand/agent-*
- 语法高亮：syntax-regexp/primitive/property/type/punctuation/object/diff-unknown
- Markdown：link-text/emph/list-enumeration/image/code-block
- Diff 语义：surface-diff-*/text-diff-*（添加绿系 / 删除红系 / 隐藏蓝系）
- 头像 12 色：avatar-background-* / avatar-text-*（pink/mint/orange/purple/cyan/lime）

## 输入 / 输出

- 输入：`dist/tokens.json`（light/dark 双套）
- 输出：`dist/token-mapping.css`（**491 条映射**：共享色阶 109 + light/dark 语义各 191，控制台打印条数）

## 约束

- 从 `@skins/core` 读 tokens，**禁止硬编码颜色**（组件表除外——那是渠道专属值）
- token-mapping.ts 只写映射关系，不做取色
- CSS 中用 `var(--...)` 引用，skin.css 不重复定义颜色
- 核心 token 输出 6 位不透明 hex；带 alpha 仅限 COMPONENT_PALETTE

## 边界条件

1. **tokens.json 缺失**：报错退出（提示先 build-tokens）
2. **主题切换**：OpenCode 用 `data-color-scheme` 属性（light/dark），无变体、无跟随系统模式
3. **token 缺 key**：`S(key)` 返回 undefined——应在上游用 validateTokens 拦截

## 验收标准

- [x] 生成 CSS 三段，选择器全部 scoped 在 `html[data-skin]` 下
- [x] light / dark 配色正确（28 项对比度全绿，含 diff/语法色语义正确）
- [x] 换 tokens.json 重新 apply 后皮肤颜色整体跟着变
- [x] legacy 别名与 v2 变量同时覆盖（新旧版本 OpenCode 都生效）
- [x] 控制台输出映射条数（491）

## 如何判断任务已经完成

`pnpm apply:opencode` 内部生成 token-mapping.css；apply 后 OpenCode 亮/暗主题视觉一致达标。
