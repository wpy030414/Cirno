// src/cdp-inject.ts
// CDP 运行时注入层（主通道）——机制借鉴 Codex-Dream-Skin（MIT，见 NOTICE），代码自写。
//   - MSIX 包解析（Get-AppxPackage OpenAI.Codex*）
//   - kill：ChatGPT (Beta).exe / Codex (Beta).exe + codex.exe(app-server) + codex-code-mode-host
//   - COM 激活启动（IApplicationActivationManager，可传 --remote-debugging-port）
//   - /json/version 就绪轮询 + 主页面目标选择（app://-/index.html，排除 avatar-overlay）
//   - WebSocket Runtime.evaluate 注入（style 标签 + data-skin + 立绘变量 + inject.js 保活）
import { spawnSync } from 'node:child_process'
import WebSocket from 'ws'

export const DEFAULT_PORT = 9335
export const MAIN_PAGE_URL = 'app://-/index.html'

export interface CodexPackage {
  packageFullName: string
  packageFamilyName: string
  applicationId: string
  installLocation: string
}

export interface InjectPayload {
  css: string
  bootstrapBody: string
  marker: string
}

export function runPs(script: string): { code: number; stdout: string; stderr: string } {
  const r = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    encoding: 'utf8',
    windowsHide: true,
    maxBuffer: 8 * 1024 * 1024,
  })
  return { code: r.status ?? 1, stdout: r.stdout ?? '', stderr: r.stderr ?? '' }
}

/** 解析 MSIX 包（PackageFullName|PackageFamilyName|ApplicationId|InstallLocation） */
export function findCodexPackage(): CodexPackage {
  if (process.platform !== 'win32') {
    throw new Error('skins/codex currently supports win32 only (MSIX package) — macOS/Linux 待测')
  }
  const script = [
    "$ErrorActionPreference='Stop'",
    "$p = Get-AppxPackage -Name 'OpenAI.Codex*' | Sort-Object Version -Descending | Select-Object -First 1",
    "if (-not $p) { throw \"OpenAI Codex Desktop package not found — install Codex Desktop from the Microsoft Store first\" }",
    "$manifest = [xml](Get-Content (Join-Path $p.InstallLocation 'AppxManifest.xml') -Raw)",
    "$app = $manifest.Package.Applications.Application | Select-Object -First 1",
    '"$($p.PackageFullName)|$($p.PackageFamilyName)|$($app.Id)|$($p.InstallLocation)"',
  ].join('\n')
  const r = runPs(script)
  if (r.code !== 0) throw new Error('failed to resolve Codex package: ' + (r.stderr || r.stdout).trim())
  const [packageFullName, packageFamilyName, applicationId, installLocation] = r.stdout.trim().split('|')
  if (!packageFullName || !packageFamilyName || !applicationId) {
    throw new Error('unexpected package resolution output: ' + r.stdout.trim())
  }
  return { packageFullName, packageFamilyName, applicationId, installLocation }
}

/** 关闭桌面进程树（只杀桌面壳与 app-server 子进程，不碰其他 codex.exe CLI 会话） */
export function killCodex(): void {
  if (process.platform !== 'win32') throw new Error('skins/codex currently supports win32 only')
  const script = [
    "taskkill /F /IM 'ChatGPT (Beta).exe' 2>&1 | Out-String",
    "taskkill /F /IM 'Codex (Beta).exe' 2>&1 | Out-String",
    "Get-CimInstance Win32_Process -Filter \"Name='codex.exe'\" | Where-Object { $_.CommandLine -match 'app-server' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }",
    "Get-CimInstance Win32_Process -Filter \"Name='codex-code-mode-host.exe'\" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }",
  ].join('\n')
  runPs(script)
}

/** COM 激活启动（IApplicationActivationManager），支持传参（如 --remote-debugging-port） */
export function launchCodex(pkg: CodexPackage, args: string[]): number {
  if (process.platform !== 'win32') throw new Error('skins/codex currently supports win32 only')
  const aumid = pkg.packageFamilyName + '!' + pkg.applicationId
  const script = [
    'Add-Type -TypeDefinition @"',
    'using System;',
    'using System.Runtime.InteropServices;',
    'public static class CdxLauncher {',
    '  [ComImport, Guid(\"2e941141-7f97-4756-ba1d-9decde894a3d\"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]',
    '  public interface IApplicationActivationManager {',
    '    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);',
    '  }',
    '  [ComImport, Guid(\"45BA127D-10A8-46EA-8AB7-56EA9078943C\")]',
    '  public class ApplicationActivationManager {}',
    '  public static uint Launch(string aumid, string args) {',
    '    var mgr = (IApplicationActivationManager)new ApplicationActivationManager();',
    '    uint pid; int hr = mgr.ActivateApplication(aumid, args, 0, out pid);',
    '    if (hr != 0) throw new System.ComponentModel.Win32Exception(hr);',
    '    return pid;',
    '  }',
    '}',
    '"@',
    "$pid2 = [CdxLauncher]::Launch('" + aumid + "', '" + args.join(' ') + "')",
    "Write-Output ('PID:' + $pid2)",
  ].join('\n')
  const r = runPs(script)
  const m = r.stdout.match(/PID:(\d+)/)
  if (r.code !== 0 || !m) {
    throw new Error('failed to activate Codex: ' + (r.stderr || r.stdout).trim())
  }
  return Number(m[1])
}

/** 轮询 CDP 端点就绪（仅回环） */
export async function waitForCdp(port: number, timeoutMs = 45000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try {
      const res = await fetch('http://127.0.0.1:' + port + '/json/version')
      if (res.ok) return
    } catch {
      // 未就绪，继续轮询
    }
    await new Promise((resolve) => setTimeout(resolve, 500))
  }
  throw new Error('CDP endpoint not reachable on 127.0.0.1:' + port + ' within ' + timeoutMs + 'ms')
}

export interface PageTarget {
  title: string
  url: string
  webSocketDebuggerUrl: string
}

/** 从 /json/list 选择主页面目标：app://-/index.html 且无 initialRoute；兜底任意 app:// 页面 */
export function pickMainPage(targets: PageTarget[]): PageTarget | null {
  return (
    targets.find((t) => t.url === MAIN_PAGE_URL) ??
    targets.find((t) => t.url.startsWith(MAIN_PAGE_URL) && !t.url.includes('initialRoute')) ??
    targets.find((t) => t.url.startsWith(MAIN_PAGE_URL)) ??
    null
  )
}

export async function findMainPage(port: number, timeoutMs = 60000): Promise<PageTarget> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    try {
      const res = await fetch('http://127.0.0.1:' + port + '/json/list')
      const targets = (await res.json()) as PageTarget[]
      const page = pickMainPage(targets)
      if (page) return page
    } catch {
      // 继续轮询
    }
    await new Promise((resolve) => setTimeout(resolve, 500))
  }
  throw new Error('main page target not found on port ' + port + ' within ' + timeoutMs + 'ms')
}

/** 组装注入脚本（IIFE；imageScript/injectJs 为本仓库自有脚本，直接内联） */
export function buildInjectScript(payload: InjectPayload): string {
  const { css, bootstrapBody, marker } = payload
  const cssJson = JSON.stringify(css)
  const windowKey = '__' + marker.replace(/[^A-Za-z0-9_]/g, '_') + '__'
  return [
    '(function(){',
    '  try {',
    "    document.documentElement.setAttribute('data-skin', 'active');",
    "    var el = document.getElementById('" + marker + "-style');",
    "    if (!el) { el = document.createElement('style'); el.id = '" + marker + "-style'; document.head.appendChild(el); }",
    '    el.textContent = ' + cssJson + ';',
    '    window["' + windowKey + '"] = true;',
    '  } catch (e) { return "error: " + e.message; }',
    '  try { ' + bootstrapBody + ' } catch (e) {}',
    "  return 'ok';",
    '})()',
  ].join('\n')
}

/** 移除脚本（还原会话内皮肤状态） */
export function buildRemoveScript(marker: string): string {
  const windowKey = '__' + marker.replace(/[^A-Za-z0-9_]/g, '_') + '__'
  return [
    '(function(){',
    "  var el = document.getElementById('" + marker + "-style'); if (el) el.remove();",
    "  document.documentElement.removeAttribute('data-skin');",
    "  ['--background-day','--background-night','--character-left','--character-right'].forEach(function(k){ document.documentElement.style.removeProperty(k); });",
    '  delete window["' + windowKey + '"];',
    "  return 'removed';",
    '})()',
  ].join('\n')
}

function evaluateOnPage(wsUrl: string, expression: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl)
    let settled = false
    const timer = setTimeout(() => {
      if (!settled) {
        settled = true
        try { ws.close() } catch {}
        reject(new Error('CDP evaluate timed out'))
      }
    }, 30000)
    ws.on('open', () => {
      ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression, returnByValue: true } }))
    })
    ws.on('message', (data: Buffer) => {
      const msg = JSON.parse(data.toString())
      if (msg.id === 1 && !settled) {
        settled = true
        clearTimeout(timer)
        try { ws.close() } catch {}
        if (msg.error) reject(new Error(msg.error.message))
        else resolve(String(msg.result?.result?.value ?? ''))
      }
    })
    ws.on('error', (err) => {
      if (!settled) {
        settled = true
        clearTimeout(timer)
        reject(err instanceof Error ? err : new Error(String(err)))
      }
    })
  })
}

export type InjectMode = 'once' | 'verify' | 'remove'

export async function inject(port: number, payload: InjectPayload, mode: InjectMode = 'once'): Promise<string> {
  const page = await findMainPage(port)
  let expression: string
  if (mode === 'remove') {
    expression = buildRemoveScript(payload.marker)
  } else if (mode === 'verify') {
    const windowKey = '__' + payload.marker.replace(/[^A-Za-z0-9_]/g, '_') + '__'
    expression = "(function(){ return document.documentElement.getAttribute('data-skin') === 'active' && !!window['" + windowKey + "'] ? 'verified' : 'absent'; })()"
  } else {
    expression = buildInjectScript(payload)
  }
  const result = await evaluateOnPage(page.webSocketDebuggerUrl, expression)
  return result
}
