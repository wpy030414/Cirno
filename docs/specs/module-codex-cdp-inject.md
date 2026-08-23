# Spec — codex CDP 运行时注入层（cdp-inject.ts）

**对应模块**：`packages/skins/codex/src/cdp-inject.ts`

## 要实现什么

Codex Desktop（MSIX）的运行时注入器（主通道）。机制借鉴 Codex-Dream-Skin（MIT，selector 契约思路），代码自写。

## 行为应该是什么

1. `findCodexPackage()`：Get-AppxPackage OpenAI.Codex* 解析 PackageFullName/FamilyName/ApplicationId/InstallLocation（win32 专用；其他平台报错，待测）
2. `killCodex()`：taskkill ChatGPT (Beta).exe / Codex (Beta).exe + 仅杀命令行含 app-server 的 codex.exe 子进程（不碰其他 CLI 会话）
3. `launchCodex(pkg, args)`：COM `IApplicationActivationManager.ActivateApplication(AUMID, args)`（直接启动 exe 被 MSIX 应用容器拒绝——G1 实测结论），可传 `--remote-debugging-port=9335`
4. `waitForCdp()`：轮询 http://127.0.0.1:9335/json/version（仅回环）
5. `findMainPage()`：/json/list 选 `app://-/index.html`（排除 avatar-overlay 目标）
6. `inject(port, payload, once|verify|remove)`：WebSocket Runtime.evaluate 执行注入脚本——设置 `data-skin=active`、注入 `<style id=codex-skin-style>`（CSP style-src 'unsafe-inline' 放行内联样式）、执行共享 `buildBootstrapBody()`（skin-core 导出：data-skin setter + data URI 立绘变量 + inject.js 保活）；window marker 供 verify/remove

## 约束

- 不改 app.asar / WindowsApps（受保护目录）；注入为运行时行为，重启即消失（与仓库「手动重跑 apply」语义一致）
- CDP 仅回环（127.0.0.1）；端口被未知进程占用时直接失败并报告
- 注入失败只影响主通道：官方 config 通道已生效时仅警告不中断（--skip-cdp 可完全跳过）

## 验收标准

- [x] G1 门禁：COM 激活传参 + /json/version 就绪（Chrome/150 实测）
- [x] 注入后实测：data-skin=active、style 11.3KB、主面 #242424（浅色 #e2e2e2）、立绘/背景 data URI 变量就位
- [x] 保活：removeAttribute('data-skin') 后 400ms 内自动补回（inject.js MutationObserver）
- [x] 单测：pickMainPage 排序/兜底、注入/移除脚本 vm 桩 DOM 执行
