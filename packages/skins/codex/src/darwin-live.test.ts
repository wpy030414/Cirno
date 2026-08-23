// src/darwin-live.test.ts
// macOS 实机门禁（本机 exact env 锚定）：非 darwin 平台自动 skip。
// 只读验证：app 存在/bundle id/asar schema 字符串 + CDP 存活时的运行时映射扫掠。
// kill/launch 的真实执行不在本文件（避免测试重启用户 app），由 E2E 流程覆盖；
// 命令构造与决策逻辑由 darwin-contract.test.ts 跨平台守护。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import WebSocket from 'ws'
import { findCodexPackage, findMainPage } from './cdp-inject.js'

const isDarwin = process.platform === 'darwin'
const __dirname = path.dirname(fileURLToPath(import.meta.url))
const MAPPING_CSS = path.resolve(__dirname, '../dist/token-mapping.css')

/** 流式扫描大文件（app.asar 284MB 不进内存） */
function fileContains(filePath: string, needle: string): boolean {
  const fd = fs.openSync(filePath, 'r')
  try {
    const buf = Buffer.alloc(4 * 1024 * 1024)
    let overlap = ''
    let pos = 0
    for (;;) {
      const n = fs.readSync(fd, buf, 0, buf.length, pos)
      if (n === 0) break
      const chunk = overlap + buf.toString('latin1', 0, n)
      if (chunk.includes(needle)) return true
      overlap = chunk.slice(-(needle.length - 1))
      pos += n
    }
    return false
  } finally {
    fs.closeSync(fd)
  }
}

function evaluate(wsUrl: string, expression: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl)
    let settled = false
    const timer = setTimeout(() => {
      if (!settled) { settled = true; try { ws.close() } catch {}; reject(new Error('CDP evaluate timed out')) }
    }, 30000)
    ws.on('open', () => ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression, returnByValue: true } })))
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
      if (!settled) { settled = true; clearTimeout(timer); reject(err instanceof Error ? err : new Error(String(err))) }
    })
  })
}

test('live·findCodexPackage 定位 ChatGPT.app（com.openai.codex）且 app.asar 存在', { skip: !isDarwin }, () => {
  const pkg = findCodexPackage()
  assert.ok(pkg.appPath, '应定位到 ChatGPT.app')
  assert.ok(['com.openai.codex', 'com.openai.chat'].includes(pkg.bundleId ?? ''), 'bundle id 应为 Codex 系')
  assert.ok(fs.existsSync(path.join(pkg.appPath!, 'Contents', 'Resources', 'app.asar')), 'app.asar 应存在')
  assert.ok(fs.existsSync(pkg.executablePath!), '主可执行文件应存在')
})

test('live·app.asar 含 chromeTheme schema 字符串（mac 严格 schema 证据，防上游改名静默失效）', { skip: !isDarwin }, () => {
  const pkg = findCodexPackage()
  const asar = path.join(pkg.appPath!, 'Contents', 'Resources', 'app.asar')
  assert.ok(fileContains(asar, 'appearanceLightChromeTheme'), 'app.asar 应含 appearanceLightChromeTheme（配置通道契约）')
  assert.ok(fileContains(asar, 'appearanceDarkChromeTheme'), 'app.asar 应含 appearanceDarkChromeTheme')
  assert.ok(fileContains(asar, 'appearanceTheme'), 'app.asar 应含 appearanceTheme（--pin 依赖）')
})

/** 提取某选择器块的 CSS 变量表（indexOf 解析，无正则） */
function extractVarBlock(css: string, selector: string): Record<string, string> {
  const vars: Record<string, string> = {}
  let idx = 0
  while ((idx = css.indexOf(selector, idx)) !== -1) {
    const open = css.indexOf('{', idx)
    const close = css.indexOf('}', open)
    if (open === -1 || close === -1) break
    for (const part of css.slice(open + 1, close).split(';')) {
      const colon = part.indexOf(':')
      if (colon === -1) continue
      const key = part.slice(0, colon).trim()
      const val = part.slice(colon + 1).trim()
      if (key.startsWith('--')) vars[key] = val
    }
    idx = close + 1
  }
  return vars
}

test('live·CDP 运行时映射扫掠：注入后的 --color-token-* 计算值与 token-mapping.css 逐项一致', { skip: !isDarwin }, async (t) => {
  if (!fs.existsSync(MAPPING_CSS)) {
    t.skip('dist/token-mapping.css 不存在——先运行 pnpm build-mapping:codex')
    return
  }
  let page
  try {
    page = await findMainPage(9335, 8000)
  } catch {
    t.skip('CDP 未存活——先运行 pnpm apply:codex 注入皮肤（本测试为活体扫掠，未注入时跳过）')
    return
  }
  const state = JSON.parse(
    await evaluate(
      page.webSocketDebuggerUrl,
      "JSON.stringify({ cls: document.documentElement.className, skin: document.documentElement.getAttribute('data-skin') })"
    )
  ) as { cls: string; skin: string | null }
  if (state.skin !== 'active') {
    t.skip('data-skin 未激活——先运行 pnpm apply:codex 注入皮肤')
    return
  }
  const mode = state.cls.includes('electron-dark') ? 'dark' : state.cls.includes('electron-light') ? 'light' : null
  if (!mode) {
    t.skip('页面未处于 electron-light/dark 模式（当前 classes: ' + state.cls + '）')
    return
  }

  const css = fs.readFileSync(MAPPING_CSS, 'utf8')
  const shared = extractVarBlock(css, 'html[data-skin]')
  const modeBlock = extractVarBlock(css, 'html[data-skin].electron-' + mode)
  const expected: Record<string, string> = { ...shared, ...modeBlock }

  // 只比对 6/8 位 hex 字面值（var()/calc 引用值不属于映射产物断言范围）
  const hexVars = Object.entries(expected).filter(([, v]) => /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(v))
  assert.ok(hexVars.length >= 20, '映射块应含至少 20 个 hex 变量（实际 ' + hexVars.length + '）')

  const keys = hexVars.map(([k]) => k)
  const live = JSON.parse(
    await evaluate(
      page.webSocketDebuggerUrl,
      'JSON.stringify(Object.fromEntries(' +
        JSON.stringify(keys) +
        '.map(function(k){ return [k, getComputedStyle(document.documentElement).getPropertyValue(k).trim()]; })))'
    )
  ) as Record<string, string>

  const mismatches: string[] = []
  for (const [key, expectedValue] of hexVars) {
    const actual = (live[key] ?? '').toLowerCase()
    if (actual !== expectedValue.toLowerCase()) {
      mismatches.push(key + ': expected ' + expectedValue + ' got ' + (actual || '(missing)'))
    }
  }
  assert.deepEqual(mismatches, [], '运行时映射与 token-mapping.css 不一致（' + mode + ' 模式，' + mismatches.length + ' 项）')
})
