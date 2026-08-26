#!/usr/bin/env node
// dev.mjs
// 关闭 OpenCode，带 --enable-devtools 重启（调试用）

import { execSync, spawn } from 'node:child_process'

const APP_DIR = '/Applications/OpenCode.app'
const PROCESS_NAME = 'OpenCode'

console.log('🔧 OpenCode Dev Launcher')
console.log('========================\n')

// 1. Kill
console.log('[dev] killing OpenCode processes...')
try {
  execSync(`pkill -f ${PROCESS_NAME}`, { stdio: 'pipe' })
  console.log('[dev] pkill sent')
} catch {
  console.log('[dev] no OpenCode process found')
}

// 2. Wait for exit
console.log('[dev] waiting for process to exit...')
const start = Date.now()
while (Date.now() - start < 5000) {
  try {
    execSync(`pgrep -f ${PROCESS_NAME}`, { stdio: 'pipe' })
    execSync('sleep 0.1', { stdio: 'pipe' })
  } catch {
    break
  }
}
console.log('[dev] process terminated')

// 3. Relaunch with devtools
console.log('[dev] launching with --enable-devtools...')
spawn('open', ['-a', APP_DIR, '--args', '--enable-devtools'], {
  detached: true,
  stdio: 'ignore'
}).unref()

console.log('[dev] ✓ OpenCode restarted with DevTools enabled')
console.log('[dev] press Option+Cmd+I to open DevTools')
