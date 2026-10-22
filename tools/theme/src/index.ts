// tools/theme/src/index.ts
// 主题管线入口：取色 → tokens.json（46 token × light/dark）→ WinUI Theme.xaml + 资产。
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { extractColorsFromImages } from './extract-colors.js'
import { generateTokenPairs } from './generate-tokens.js'
import {
  getColorSourceImagePaths,
  getCharConfig,
  getThemeDir,
  getThemeMeta,
  getThemePackageDir,
  listThemes,
  selectTheme,
  DEFAULT_CHAR_CONFIG,
} from './assets-loader.js'
import { emitTheme, writeThemeRegistry, type TokenPair } from './emit-xaml.js'
import type { BuildOptions } from './types.js'

// 供测试与其他工具复用的取色/派生工具
export {
  rgbToHex,
  hexToRgb,
  rgbToHsl,
  hslToRgb,
  adjustLightness,
  lerpRgb,
  generateScale,
  generateTokens,
  generateTokenPairs,
} from './generate-tokens.js'
export {
  getAllRoleImagePaths,
  getColorSourceImagePaths,
  listThemes,
  selectTheme,
  getThemeDir,
  getThemeMeta,
  getActiveTheme,
  getCharConfig,
  getThemesRoot,
  getRepoRoot,
  getThemePackageDir,
  DEFAULT_CHAR_CONFIG,
  DEFAULT_ART,
} from './assets-loader.js'
export { buildThemeXaml, renderThemeDictionary, emitTheme, writeThemeRegistry } from './emit-xaml.js'
export { TOKEN_KEYS } from './types.js'
export type { Tokens, TokenKey, RGB, Hex, ColorCluster, BuildOptions } from './types.js'
export type { ArtConfig, ThemeMeta } from './assets-loader.js'
export type { TokenPair, EmitResult } from './emit-xaml.js'

/**
 * 从图片取色并生成 light/dark 双套 token，可选写入 tokens.json。
 */
export async function buildTokens(options: BuildOptions): Promise<TokenPair> {
  const { sources, k = 8, outPath, theme, charConfig } = options

  console.log('  取色中…')
  console.log(`  源图：${sources.length} 张 · 聚类：${k} 簇`)

  const clusters = await extractColorsFromImages(sources, k)
  console.log(`  得到 ${clusters.length} 个颜色簇`)

  const { light, dark } = generateTokenPairs(clusters)
  console.log(`  生成 light/dark 各 ${Object.keys(light).length} 个 token`)

  if (outPath) {
    const dir = path.dirname(outPath)
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true })

    const out: Record<string, unknown> = {
      'char-config': {
        'character-left': {
          offset: charConfig?.['character-left']?.offset ?? DEFAULT_CHAR_CONFIG['character-left'].offset,
          height: charConfig?.['character-left']?.height ?? DEFAULT_CHAR_CONFIG['character-left'].height,
        },
        'character-right': {
          offset: charConfig?.['character-right']?.offset ?? DEFAULT_CHAR_CONFIG['character-right'].offset,
          height: charConfig?.['character-right']?.height ?? DEFAULT_CHAR_CONFIG['character-right'].height,
        },
      },
      light,
      dark,
    }
    if (theme) out.theme = theme
    fs.writeFileSync(outPath, JSON.stringify(out, null, 2))
    console.log(`  ✓ ${outPath}`)
  }

  return { light, dark }
}

/** 构建单个主题：取色 → generated/tokens.json → generated/Theme.xaml */
export async function buildTheme(theme: string, opts: { k?: number } = {}) {
  const meta = getThemeMeta(theme)
  const sources = getColorSourceImagePaths(theme)
  const outDir = getThemePackageDir(theme)

  const pair = await buildTokens({
    sources,
    k: opts.k ?? 16,
    theme: meta.id,
    charConfig: getCharConfig(theme),
    outPath: path.join(outDir, 'tokens.json'),
  })
  const emitted = await emitTheme(theme, pair, outDir)
  return { meta, emitted }
}

// ── CLI ────────────────────────────────────────────────────────────────────
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2)
  let themeArg: string | undefined
  let all = false
  let k = 16

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--theme' && args[i + 1]) themeArg = args[++i]
    else if (args[i] === '--all') all = true
    else if (args[i] === '--k' && args[i + 1]) k = parseInt(args[++i], 10)
  }

  try {
    const themes = all ? listThemes() : [await selectTheme(themeArg)]
    if (themes.length === 0) throw new Error('themes/ 下没有任何 <id>.theme/ 主题')

    for (const theme of themes) {
      const { meta, emitted } = await buildTheme(theme, { k })
      console.log(`✓ 主题「${meta.displayName}」(${meta.id})`)
      console.log(`   XAML   ${path.relative(process.cwd(), emitted.xamlPath)}`)
      console.log(`   元数据 ${path.relative(process.cwd(), emitted.infoPath)}`)
    }

    // 注册表汇总所有已发射主题（含本次未构建的），app 主题选择器据此列出
    const infoPaths = listThemes()
      .map((t) => path.join(getThemePackageDir(t), 'ThemeInfo.json'))
      .filter((p) => fs.existsSync(p))
    if (infoPaths.length > 0) {
      const registry = writeThemeRegistry(infoPaths, path.resolve('dist/themes.json'))
      console.log(`   注册表 ${path.relative(process.cwd(), registry)}（${infoPaths.length} 个主题）`)
    }
    process.exit(0)
  } catch (err) {
    console.error('\n主题构建失败：', (err as Error).message)
    process.exit(1)
  }
}
