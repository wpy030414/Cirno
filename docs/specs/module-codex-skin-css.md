# Spec — codex 主题 CSS（skin.css）

**对应模块**：`packages/skins/codex/src/skin.css`

## 要实现什么

一份组件装饰文件：主内容表面挂宫殿背景（昼/夜随 `electron-light`/`electron-dark`）+ 角色立绘贴底。**所有颜色由 token-mapping.css 提供，本文件不硬编码颜色**（测试断言）。

## 行为应该是什么

`<html data-skin="active">` 存在时：

1. `main[data-app-shell-main-surface]` 建立定位上下文（position: relative）
2. `::before`：宫殿背景（`var(--background-day)`/`var(--background-night)`，opacity 0.24，D-013 同档），z-index 0、pointer-events none
3. `::after`：角色立绘（`var(--character-left/right)` + `--character-*-position/height`），z-index 1、pointer-events none
4. 直接子元素 z-index 2 置顶（opencode 同原则；内容承载区不透明，D-020）
5. `[role="dialog"]` 内不渲染装饰层（content: none）
6. 删除 `data-skin` → 全部规则失效 → 原生还原

## 约束

- 全部选择器 scoped 在 `html[data-skin]` 下
- 不硬编码颜色与立绘路径（var() 消费）
- 选择器最小集 + 安全降级（目标 DOM 变动时装饰层自动失效，不影响原生 UI）

## 验收标准

- [x] skin.css 无 hex/rgb/hsl 字面量（测试断言）
- [x] 实机暗色/浅色下主面装饰与立绘渲染正常（视觉评测 PASS）
- [x] 项目选择对话框（[role=dialog]）内无装饰层（像素事实：暖色占比 0.70%）
- [x] 删除 data-skin 后原生还原（还原验收通过）
