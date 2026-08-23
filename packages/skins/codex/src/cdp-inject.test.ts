// src/cdp-inject.test.ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import vm from 'node:vm'
import {
  MAIN_PAGE_URL,
  buildInjectScript,
  buildRemoveScript,
  pickMainPage,
  type PageTarget,
} from './cdp-inject.js'

test('pickMainPage：优先精确 URL，排除 initialRoute，兜底，空列表 null', () => {
  const main: PageTarget = { title: 'Codex', url: MAIN_PAGE_URL, webSocketDebuggerUrl: 'ws://x/main' }
  const overlay: PageTarget = { title: 'Codex', url: MAIN_PAGE_URL + '?initialRoute=%2Favatar-overlay', webSocketDebuggerUrl: 'ws://x/overlay' }
  assert.equal(pickMainPage([overlay, main])?.webSocketDebuggerUrl, 'ws://x/main')
  assert.equal(pickMainPage([overlay])?.webSocketDebuggerUrl, 'ws://x/overlay')
  assert.equal(pickMainPage([]), null)
})

test('buildInjectScript：在 vm 桩 DOM 中执行返回 ok，设置 data-skin/style/marker', () => {
  const calls: string[] = []
  const context: any = {}
  const doc = {
    documentElement: {
      setAttribute: (k: string, v: string) => calls.push('setAttr:' + k + '=' + v),
      style: { setProperty: (k: string, v: string) => calls.push('setProp:' + k) },
    },
    head: { appendChild: (el: any) => calls.push('append:' + el.id) },
    createElement: () => ({ id: '', textContent: '' }),
    getElementById: () => null,
  }
  const windowObj: Record<string, unknown> = {}
  const ctx = vm.createContext({ document: doc, window: windowObj })
  const script = buildInjectScript({
    css: '.a { color: #123456 }',
    bootstrapBody: ';(function(){})();',
    marker: 'codex-skin',
  })
  const result = vm.runInNewContext(script, ctx)
  assert.equal(result, 'ok')
  assert.ok(calls.includes("setAttr:data-skin=active"), String(calls))
  assert.ok(calls.includes('append:codex-skin-style'), String(calls))
  assert.ok(windowObj['__codex_skin__'] === true, 'window marker 未设置')
})

test('buildInjectScript：CSS 经 JSON 序列化，含引号与换行安全', () => {
  const script = buildInjectScript({
    css: 'html[data-skin] {\n  --x: "quoted";\n}',
    bootstrapBody: '',
    marker: 'm',
  })
  const text = script
  assert.ok(text.includes('--x: \\"quoted\\"'.replace('\\\\', '\\')), 'CSS 引号转义不正确')
  assert.ok(text.includes("id = 'm-style'"), 'style id 不正确')
})

test('buildRemoveScript：删除 style、移除属性、清理变量', () => {
  const calls: string[] = []
  const doc = {
    documentElement: {
      removeAttribute: (k: string) => calls.push('removeAttr:' + k),
      style: { removeProperty: (k: string) => calls.push('removeProp:' + k) },
    },
    getElementById: () => ({ remove: () => calls.push('styleRemoved') }),
  }
  const windowObj: any = { __codex_skin__: true }
  const ctx = vm.createContext({ document: doc, window: windowObj })
  const result = vm.runInNewContext(buildRemoveScript('codex-skin'), ctx)
  assert.equal(result, 'removed')
  assert.ok(calls.includes('styleRemoved'))
  assert.ok(calls.includes('removeAttr:data-skin'))
  assert.equal(windowObj['__codex_skin__'], undefined)
})
