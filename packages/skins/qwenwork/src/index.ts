// src/index.ts
// Entry point for the skin asar patcher.
// Run with: pnpm apply (or pnpm apply -- --no-force / --no-backup / --allow-running)
// 主题不在此选择——apply 跟随 build-tokens 选定的活动主题
import { patchAsar } from './patch-asar.js'
import { getActiveTheme } from '@skins/core/assets-loader'

async function main(): Promise<void> {
  const theme = getActiveTheme()
  console.log(`🐾 Universal Skin Patcher (QwenWork Edition)`)
  console.log('=============================================\n')
  console.log(`   Active theme: ${theme}\n`)

  const args = process.argv.slice(2)
  const force = !args.includes('--no-force')
  const noBackup = args.includes('--no-backup')
  const allowRunning = args.includes('--allow-running')

  // --app-dir <path>  overrides the auto-detected install directory
  const appDirIdx = args.indexOf('--app-dir')
  const appDir = appDirIdx !== -1 ? args[appDirIdx + 1] : undefined

  const options: Parameters<typeof patchAsar>[0] = {
    force,
    noBackup,
    allowRunning,
    appDir,
  }

  await patchAsar(options)
}

main().catch((err) => {
  console.error('\n❌ Patch failed:', err.message)
  process.exit(1)
})
