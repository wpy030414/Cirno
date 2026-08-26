# Spec — codex token 映射（token-mapping.ts）

**对应模块**：`packages/skins/codex/src/token-mapping.ts`

## 要实现什么

把 skin-core 的 46 个通用 tokens（light/dark 双套）映射到 Codex Desktop 的三个通道，生成三个 dist 产物：

1. `dist/token-mapping.css`（主通道）——CDP 运行时注入到 Codex webview 的 `--color-token-*` 设计变量（实测自 Codex 26.727 的样式表），三段结构：`html[data-skin]`（共享色阶 + char-config）/ `html[data-skin].electron-light` / `html[data-skin].electron-dark`（webview 的明暗标记是 `<html>` 的 class）
2. `dist/token-mapping.toml`（辅通道）——config.toml 官方 chrome 层：`[desktop.appearanceLightChromeTheme]` / `[desktop.appearanceDarkChromeTheme]` 的颜色键（accent/ink/surface + semanticColors.diffAdded/diffRemoved/skill）。G2 实测该通道只影响原生窗口表面/背景，UI 内容不变色
3. `dist/codex-theme-v1-light.txt` / `dark.txt`——官方 Settings → Appearance → Import 导入串（云持久化，用户手动导入；导入时自行选择内置 codeThemeId）

## 映射语义

- 表面：bg-primary/main-surface-primary ← surface-base；side-bar/editor ← surface-weak；dropdown/menu/button ← surface-raised~strong 分档
- 文字：foreground/input/editor ← text-strong；description/placeholder ← text-weak；disabled ← text-weaker；button/checkbox-foreground ← text-strong（text-inverse 在浅色浅灰按钮上对比度 1.15:1，被 contrast 测试拦截后修正）
- 强调：primary/link ← brandLink（light brand-600 / dark brand-500，D-018）；on-accent 恒 #FFFFFF；focus-border/list-focus-outline ← brandLink+α0.76（组件表）
- 语义：diffAdded ← success-base、diffRemoved ← critical-base、skill ← accent（对比度 <3 时 fallback info-base）；git-decoration/charts 同族分档
- 组件表（COMPONENT_PALETTE）：渠道专属的 alpha/层级选择，色相来自 tokens、透明度档位参照 Codex 原生体系（0.04/0.08/0.20/0.40/0.71/0.76/0.96 等）；**8 位带 alpha hex 仅限此表**

## 约束

- 颜色全部来自 tokens.json；本文件不硬编码颜色（`#FFFFFF` 恒浅常量除外）
- 核心 token 值 6 位 hex；alpha 仅组件表
- skin.css 不重复定义颜色（测试断言 skin.css 无 hex/rgb 字面量）

## 验收标准

- [x] `pnpm apply:codex` 自动生成三个产物（css 共享 6 + light 117 + dark 117 = 240 条映射；含 --oai-wb-*/composer/mermaid/vscode/referral 等全家族）
- [x] 弹层表面分档修正：dropdown/menu/checkbox/editor-widget 不再误用反色 surface-strong（白对话框根除，亮度方向回归断言兜底）
- [x] 三段选择器结构与 electron-light/electron-dark 主题类实测一致
- [x] 全部映射值合法（6/8 位 hex 或 var()/calc），无 undefined
- [x] chromeTheme 双套 12 个 6 位 hex；theme-v1 导入串可 JSON 解析
- [x] 对比度断言：ink/surface ≥4.5、accent/diff/skill ≥3（light+dark 双套，fixture tokens）
