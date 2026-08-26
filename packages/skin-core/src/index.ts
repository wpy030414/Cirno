// packages/skin-core/src/index.ts
// Entry point: build design tokens from source images.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { extractColorsFromImages } from './extract-colors.js'
import { generateTokenPairs } from './generate-tokens.js'
import { getAllRoleImagePaths, getColorSourceImagePaths, listThemes, selectTheme, getThemeDir, getActiveTheme, getCharConfig, DEFAULT_CHAR_CONFIG } from './assets-loader.js'
import type { BuildOptions } from './types.js'

// Re-export color utilities for skin packages to derive their own scales.
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
export { getAllRoleImagePaths, getColorSourceImagePaths, listThemes, selectTheme, getThemeDir, getActiveTheme, getCharConfig, DEFAULT_CHAR_CONFIG } from './assets-loader.js'
export { buildBootstrap, buildBootstrapBody, buildImageInjectionScript, imageFileToDataUri, imageFileToOptimizedDataUri } from './bootstrap-builder.js'
export { TOKEN_KEYS } from './types.js'
export type { Tokens, TokenKey, RGB, Hex, ColorCluster, BuildOptions } from './types.js'

/**
 * Build design tokens from source images.
 * Generates BOTH light and dark token sets from the same clusters.
 *
 * @param options - Build configuration
 * @returns The generated token pairs { light, dark }
 */
export async function buildTokens(options: BuildOptions): Promise<{ light: Record<string, string>; dark: Record<string, string> }> {
  const { sources, k = 8, outPath, theme, charConfig } = options

  console.log('🎨 Skin Core: Extracting colors from images...')
  console.log(`   Sources: ${sources.length} images`)
  console.log(`   Clusters: ${k}`)
  if (theme) {
    console.log(`   Theme: ${theme}`)
  }

  // Extract colors from all images
  const clusters = await extractColorsFromImages(sources, k)

  console.log(`   Extracted ${clusters.length} color clusters`)

  // Generate light + dark token pairs
  const { light, dark } = generateTokenPairs(clusters)

  console.log('   Generated tokens (light):')
  for (const [key, value] of Object.entries(light)) {
    console.log(`     ${key}: ${value}`)
  }
  console.log('   Generated tokens (dark):')
  for (const [key, value] of Object.entries(dark)) {
    console.log(`     ${key}: ${value}`)
  }

  // Write to file if outPath specified
  if (outPath) {
    const dir = path.dirname(outPath)
    if (!fs.existsSync(dir)) {
      fs.mkdirSync(dir, { recursive: true })
    }
    // 活动主题记录在 tokens.json 顶层 theme 字段——preview / apply 跟随读取；
    // 角色展示配置（char-config）同样写顶层，供 apply 时生成 CSS 变量
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
    console.log(`   ✓ Wrote tokens (light+dark) to ${outPath}`)
  }

  return { light, dark }
}

// CLI mode: run only when executed directly (not when imported)
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2)

  // Parse --sources / --out / --k / --theme flags
  let sources: string[] = []
  let outPath = 'dist/tokens.json'
  let k = 16
  let themeArg: string | undefined

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--sources' && i + 1 < args.length) {
      sources = args[i + 1].split(',')
      i++
    } else if (args[i] === '--out' && i + 1 < args.length) {
      outPath = args[i + 1]
      i++
    } else if (args[i] === '--k' && i + 1 < args.length) {
      k = parseInt(args[i + 1])
      i++
    } else if (args[i] === '--theme' && i + 1 < args.length) {
      themeArg = args[i + 1]
      i++
    }
  }

  let theme: string | undefined
  let charConfig: BuildOptions['charConfig']
  try {
    // If no --sources provided, load from the selected theme's manifest colorSource
    // (character art only — scene backgrounds must NOT tint the theme)
    if (sources.length === 0) {
      theme = await selectTheme(themeArg)
      sources = getColorSourceImagePaths(theme)
      console.log(`   Using theme '${theme}' colorSource (${sources.length} images)`)
      charConfig = getCharConfig(theme)
      const left = charConfig['character-left']!
      const right = charConfig['character-right']!
      console.log(
        `   Char config: left offset [${left.offset}] h ${left.height} / right offset [${right.offset}] h ${right.height}`
      )
    }

    await buildTokens({ sources, k, outPath, theme, charConfig })
    console.log('\n✓ Token generation complete')
    process.exit(0)
  } catch (err) {
    console.error('\n❌ Token generation failed:', (err as Error).message)
    process.exit(1)
  }
}
