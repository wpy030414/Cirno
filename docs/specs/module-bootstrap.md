# Spec — 共享 bootstrap 注入与属性保活

**对应模块**：`skin-core/src/bootstrap-builder.ts` + `skin-core/src/inject.js`

## 要实现什么

1. 构建时生成统一的 HTML 注入片段（内联 CSS + 立绘 data URI + 保活脚本）
2. 运行时设置 `<html data-skin="active">`、注入 4 个立绘 CSS 变量、保活 `data-skin` 属性

所有 skin 包共享这套逻辑，不重复实现。

## 行为应该是什么（构建时）

`buildBootstrap({ css, injectJs, marker })` 产出：

```html
<!-- {marker} start -->
<style id="{marker}-style">
  /* token-mapping.css + skin.css 合并 */
</style>
<script id="{marker}-script">
  ;(function(){ document.documentElement.dataset.skin = "active" })();
  ;(function(){
    var skinImages = window.__skinImages = {};
    skinImages['--background-day'] = 'data:image/webp;base64,...';  // ×4
    function applyImage(varName) {
      if (!skinImages[varName]) return;
      document.documentElement.style.setProperty(varName, "url(" + skinImages[varName] + ")");
      delete skinImages[varName];
    }
    setTimeout(function(){ applyImage('--background-day'); }, 1);  // ×4, 延迟防 OOM
  })();
  /* inject.js: MutationObserver 保活 */
</script>
<!-- {marker} end -->
```

`injectBootstrapIntoHtml(html, bootstrap)`：在 `</head>` 之前插入（`</head>` 缺失抛错）。

`buildBootstrapBody(injectJs)`：共享 bootstrap body（data-skin setter + 立绘注入脚本 + inject.js），不含 HTML 包装——供 CDP 运行时注入类渠道（skins/codex）复用，避免重复实现注入逻辑。

## 行为应该是什么（运行时）

1. `document.documentElement.dataset.skin = "active"`
2. 4 个 CSS 变量以 `url(data:...)` 注入（`setTimeout(1ms)` 延迟——避免一次注入大图触发 Electron OOM watchdog）
3. `inject.js` 启动 MutationObserver 保活

`inject.js` 细节：
- IIFE + `'use strict'`，不污染全局
- `ensureSkinAttr()`：值不为 `"active"` 就设回 `"active"`
- `document.readyState === 'loading'` 时等 `DOMContentLoaded` 再启动
- MutationObserver 监听 `<html>` 的 `attributes`，`attributeFilter: ['data-skin']`
- 使用 `var`（非 `let/const`），ES5 语法，兼容旧版 Electron

## 输入

- `css`：token-mapping.css + skin.css 合并文本
- `injectJs`：共享 inject.js 内容
- `marker`：skin 包自己的 marker（`qwenwork-skin` / `oc-skin`）
- 素材：**活动主题**（build-tokens 写入 tokens.json 的 theme 字段）的 4 个角色——构建时经 `getActiveTheme()` 读取，转 data URI base64。**主题不在此选择**
- **大图压缩**：data URI 超过 1.5MB 的图片（base64 后）构建时用 sharp 缩放（上限 1920×1080，不放大）+ 转 webp（quality 82）——**Chromium 对 URL 有 2MB 硬上限（kMaxURLChars），超长会被静默替换为无效 URL，背景图直接消失**；小图原样内联零损失

## 输出

- 注入片段（HTML 字符串）
- 运行时：`data-skin` 属性常驻 + 4 个 CSS 变量常驻

## 约束

- 不依赖目标 app 的任何全局变量
- 不修改 React 的虚拟 DOM
- 不引入远程资源（data URI 内联）
- 所有图片缺失时报错（`missing asset: <path>`）
- **每张图 data URI < 2MB**（Chromium URL 上限）——超限图构建时自动压缩转 webp

## 边界条件

1. **`document.readyState` 已不是 'loading'**：直接 start，不等待
2. **MutationObserver 不可用**（极老 Electron）：属性保活失效，但皮肤仍能首次生效（可接受）
3. **`data-skin` 被外部代码设为其他值**：observer 触发后强制改回 `active`
4. **`</head>` 缺失**：注入前抛错，拒绝 patch

## 验收标准

- [x] 启动后 `<html>` 上有 `data-skin="active"` 属性
- [x] 4 个 CSS 变量值为有效的 `url(data:...)`
- [x] React 重渲染（如切换主题）后属性仍在
- [x] 控制台无报错
- [x] DevTools 里手动 `removeAttribute('data-skin')` 后立刻被补回

## 如何判断任务已经完成

apply 后目标 app 皮肤生效；DevTools 手动删属性立即恢复；无 OOM、无 CSP 报错。
