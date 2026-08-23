# Spec — codex CDP 运行时注入层（cdp-inject.ts）

**对应模块**：`packages/skins/codex/src/cdp-inject.ts`

## 要实现什么

Codex Desktop（MSIX）的运行时注入器（主通道）。机制借鉴 Codex-Dream-Skin（MIT，selector 契约思路），代码自写。

## 行为应该是什么

1. `findCodexPackage()`：
   - win32：Get-AppxPackage OpenAI.Codex* 解析 PackageFullName/FamilyName/ApplicationId/InstallLocation
   - darwin：`findDarwinCodexApp()` 在 /Applications、~/Applications 定位 ChatGPT.app（bundle id com.openai.codex，macOS Codex Desktop 载体，Electron），校验 Contents/MacOS/ChatGPT 存在；bundle id 不符（com.openai.codex / com.openai.chat 之外）的同名应用跳过
   - linux：报错，待测
2. `killCodex()`：
   - win32：taskkill ChatGPT (Beta).exe / Codex (Beta).exe + 仅杀命令行含 app-server 的 codex.exe 子进程（不碰其他 CLI 会话）
   - darwin：pkill -x ChatGPT（优雅终止，5s 超时后 -9 兜底）+ pkill -f '[C]hatGPT.app/Contents/Resources/codex'（仅杀应用包内 app-server/code-code-mode-host；[C] 前缀防匹配自身命令行，不碰用户 codex CLI）
3. `launchCodex(pkg, args)`：
   - win32：COM `IApplicationActivationManager.ActivateApplication(AUMID, args)`（直接启动 exe 被 MSIX 应用容器拒绝——G1 实测结论）
   - darwin：`open -a <appPath> --args <args>`（open 不返回 pid，就绪由 waitForCdp 轮询确认）
   - 均可传 `--remote-debugging-port=9335`
4. `waitForCdp()`：轮询 http://127.0.0.1:9335/json/version（仅回环）
5. `findMainPage()`：/json/list 选 `app://-/index.html`（排除 avatar-overlay 目标）
6. `inject(port, payload, once|verify|remove)`：WebSocket Runtime.evaluate 执行注入脚本——设置 `data-skin=active`、注入 `<style id=codex-skin-style>`（CSP style-src 'unsafe-inline' 放行内联样式）、执行共享 `buildBootstrapBody()`（skin-core 导出：data-skin setter + data URI 立绘变量 + inject.js 保活）；window marker 供 verify/remove

## 约束

- 不改 app.asar / WindowsApps（受保护目录）；注入为运行时行为，重启即消失（与仓库「手动重跑 apply」语义一致）
- CDP 仅回环（127.0.0.1）；端口被未知进程占用时直接失败并报告
- 注入失败只影响主通道：官方 config 通道已生效时仅警告不中断（--skip-cdp 可完全跳过）

## 验收标准

- [x] G1 门禁：COM 激活传参 + /json/version 就绪（Chrome/150 实测）
- [x] G1 门禁（macOS 26.818.41705 实测）：findDarwinCodexApp 定位 /Applications/ChatGPT.app（com.openai.codex）→ pkill 关闭 → open -a --args 启动 → /json/version 就绪（Chrome/151.0.7922.170），主页面 app://-/index.html（与 win32 相同 URL）
- [x] 注入后实测：data-skin=active、style 11.3KB、主面 #242424（浅色 #e2e2e2）、立绘/背景 data URI 变量就位
- [x] 注入后实测（macOS）：data-skin=active、style 13.9KB、主面 rgb(36,36,36)（暗）/ rgb(226,226,226)（浅）、四枚立绘/背景 data URI 变量就位、--codex-base-* 跟随 chromeTheme
- [x] 保活：removeAttribute('data-skin') 后 400ms 内自动补回（inject.js MutationObserver；macOS 实测 1.2s 内补回）
- [x] 单测：pickMainPage 排序/兜底、注入/移除脚本 vm 桩 DOM 执行
- [x] 单测（darwin 分支）：findDarwinCodexApp 定位/缺失/跳过 bundle id 不符应用、buildDarwinLaunchArgs 参数组装（26 项全绿）
