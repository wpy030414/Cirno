// src/index.ts
// Codex Desktop 皮肤应用入口。Run with: pnpm apply:codex
// 参数：--no-force / --no-backup / --allow-running / --pin light|dark / --skip-cdp / --port N / --restore
// 主题不在此选择——apply 跟随 build-tokens 选定的活动主题（getActiveTheme）
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { getActiveTheme } from '@skins/core/assets-loader'
import { buildBootstrapBody } from '@skins/core/bootstrap-builder'
import {
  generateChromeThemeColors,
  generateChromeThemeToml,
  generateTokenMappingCSS,
  generateThemeV1String,
  type TokenPairs,
} from './token-mapping.js'
import { patchConfigFile, restoreConfigFile } from './patch-config.js'
import {
  DEFAULT_PORT,
  findCodexPackage,
  killCodex,
  launchCodex,
  waitForCdp,
  inject,
} from './cdp-inject.js'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const MARKER = 'codex-skin'
const configPath = path.join(os.homedir(), '.codex', 'config.toml')
const backupPath = configPath + '.skin.bak'

function readArgValue(name: string): string | null {
  const i = process.argv.indexOf(name)
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : null
}

async function main(): Promise<void> {
  const args = process.argv.slice(2)
  const force = !args.includes('--no-force')
  const noBackup = args.includes('--no-backup')
  const allowRunning = args.includes('--allow-running')
  const skipCdp = args.includes('--skip-cdp')
  const restore = args.includes('--restore')
  const pinArg = readArgValue('--pin')
  const pin = pinArg === 'light' || pinArg === 'dark' ? pinArg : null
  const port = Number(readArgValue('--port') ?? DEFAULT_PORT)

  const theme = getActiveTheme()
  console.log('🐾 Universal Skin Patcher (Codex Edition)')
  console.log('=============================================')
  console.log('   Active theme: ' + theme)

  if (restore) {
    console.log('')
    console.log('↩  Restoring config.toml from .skin.bak...')
    restoreConfigFile(configPath, backupPath)
    console.log('   ✓ config.toml restored')
    if (!allowRunning) {
      killCodex()
      const pkg = findCodexPackage()
      launchCodex(pkg, [])
      console.log('   ✓ Codex restarted (native, no skin)')
    }
    return
  }

  // 1) tokens（light/dark 双套，跟随活动主题）
  const tokensPath = path.resolve(__dirname, '../../../skin-core/dist/tokens.json')
  if (!fs.existsSync(tokensPath)) {
    throw new Error("tokens.json not found — run 'pnpm build-tokens' first (theme is selected there)")
  }
  const tokens = JSON.parse(fs.readFileSync(tokensPath, 'utf8')) as TokenPairs

  // 2) 生成映射产物（CSS + TOML + 官方导入串）—— 原 build-mapping 步骤，现内联
  const distDir = path.resolve(__dirname, '../dist')
  fs.mkdirSync(distDir, { recursive: true })

  const mappingCss = generateTokenMappingCSS(tokens)
  fs.writeFileSync(path.join(distDir, 'token-mapping.css'), mappingCss)

  const chromeColors = generateChromeThemeColors(tokens)
  fs.writeFileSync(path.join(distDir, 'token-mapping.toml'), generateChromeThemeToml(tokens))
  fs.writeFileSync(path.join(distDir, 'codex-theme-v1-light.txt'), generateThemeV1String(tokens, 'light'))
  fs.writeFileSync(path.join(distDir, 'codex-theme-v1-dark.txt'), generateThemeV1String(tokens, 'dark'))

  console.log('✓ Generated dist/token-mapping.css + token-mapping.toml + theme import strings')
  const skinCss = fs.readFileSync(path.resolve(__dirname, './skin.css'), 'utf8')
  const injectJs = fs.readFileSync(path.resolve(__dirname, '../../../skin-core/src/inject.js'), 'utf8')

  // 3) 官方 chrome 层（config.toml；只写颜色键，其余用户键保留）
  console.log('')
  console.log('🎨 Patching config.toml chromeTheme (light+dark color keys)...')
  const result = patchConfigFile(configPath, chromeColors.light, chromeColors.dark, { force, noBackup, pin })
  console.log('   ✓ config.toml patched' + (result.backedUp ? ' (backup created: ' + result.backupPath + ')' : ''))

  // 4) CDP 注入层（主通道：webview UI 颜色 + 立绘 + 保活）
  if (skipCdp) {
    console.log('   ⚠ --skip-cdp: 仅写入 config.toml（原生 chrome 层）。重启 Codex 后运行 apply 注入 UI 配色。')
    console.log('还原：pnpm apply:codex --restore')
    return
  }
  const pkg = findCodexPackage()
  if (!allowRunning) killCodex()
  console.log('🚀 Launching Codex with CDP (port ' + port + ')...')
  launchCodex(pkg, ['--remote-debugging-port=' + port])
  await waitForCdp(port)
  console.log('💉 Injecting skin (colors + character art + keep-alive)...')
  const bootstrapBody = await buildBootstrapBody(injectJs)
  const result2 = await inject(port, { css: mappingCss + '\n' + skinCss, bootstrapBody, marker: MARKER }, 'once')
  if (result2 !== 'ok') {
    throw new Error('injection failed: ' + result2)
  }
  console.log('   ✓ Skin injected')

  console.log('')
  console.log('还原：pnpm apply:codex --restore（或关闭 Codex 后把 ' + backupPath + ' 覆盖回 config.toml）')
  console.log('注意：UI 配色与立绘为运行时注入，Codex 由 apply 启动时生效；自行启动 Codex 时仅原生 chrome 层生效，重跑 apply 即可。')
}

main().catch((err) => {
  console.error('')
  console.error('❌ Patch failed:', (err as Error).message)
  console.error('还原：pnpm apply:codex --restore（或关闭 Codex 后把 ' + backupPath + ' 覆盖回 config.toml）')
  process.exit(1)
})
