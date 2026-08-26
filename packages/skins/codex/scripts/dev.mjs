#!/usr/bin/env node
// dev.mjs
// 关闭 Codex Desktop，带 CDP 调试端口重启并注入皮肤（等价于 pnpm apply），
// 完成后提示 DevTools 访问方式。前提：pnpm build-tokens 已跑过。
import { spawnSync } from 'node:child_process'

console.log('🔧 Codex Dev Launcher (apply + CDP)')
console.log('===================================')

const r = spawnSync('pnpm', ['apply'], {
  stdio: 'inherit',
  shell: false,
})
if (r.status !== 0) process.exit(r.status ?? 1)

console.log('')
console.log('[dev] CDP 已开启（默认 9335，仅回环）')
console.log('[dev] DevTools: 打开 http://127.0.0.1:9335/json/list 取 webSocketDebuggerUrl，或在 Chrome 地址栏 chrome://inspect 配置')
