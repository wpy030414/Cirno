// packages/skins/qwenwork/src/token-mapping.ts
// Map universal tokens to QwenWork's CSS variable system.
// Maintained by the qwenwork skin author, not skin-core.
// Generates three blocks:
//   html[data-skin]                    — shared brand/neutral scales
//   html[data-skin][data-theme="light"...] — light semantic variables
//   html[data-skin][data-theme="dark"...]  — dark semantic variables
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  hexToRgb,
  rgbToHex,
  rgbToHsl,
  hslToRgb,
  DEFAULT_CHAR_CONFIG,
  type Tokens,
} from '@skins/core'

interface MappingRule {
  target: string
  source: (t: TokenPairs) => string
}

type ThemeMode = 'light' | 'dark'

/** Token pair produced by skin-core: both palettes, same schema. */
type TokenPairs = {
  light: Tokens
  dark: Tokens
  /** 角色展示配置（offset/height 为 CSS 值字符串），build-tokens 从 manifest char-config 写入 */
  'char-config'?: Record<'character-left' | 'character-right', { offset: [string, string]; height: string }>
}

/** Channel-specific component palette (light/dark variants, not derived from image tokens). */
const COMPONENT_PALETTE: Record<string, { light: string; dark: string }> = {
  "--bg-sidebar-alpha": { light: "#E5EDF900", dark: "#0D1A3A00" },
  "--bg-sidebar-material": { light: "#E5EDF9d9", dark: "#0D1A3Ad9" },
  "--bg-base-alpha": { light: "#F1F5FC00", dark: "#080F2700" },
  "--bg-base-material": { light: "#F1F5FCd9", dark: "#080F27d9" },
  "--bg-card-accent": { light: "#526AA814", dark: "#8CA4DC14" },
  "--bg-card-inverted": { light: "#1A2A4D", dark: "#E5EAF6" },
  "--bg-pop-material": { light: "#FFFFFFd9", dark: "#1E3468d9" },
  "--color-shadow-2xs": { light: "#17234712", dark: "#0000001a" },
  "--color-shadow-xs": { light: "#17234712", dark: "#0000001a" },
  "--color-shadow-sm": { light: "#1723471a", dark: "#00000026" },
  "--color-shadow-md": { light: "#1723471a", dark: "#00000026" },
  "--color-shadow-lg": { light: "#17234726", dark: "#00000033" },
  "--color-shadow-xl": { light: "#17234726", dark: "#00000033" },
  "--color-shadow-2xl": { light: "#17234733", dark: "#0000004d" },
  "--color-shadow-3xl": { light: "#1723474d", dark: "#00000080" },
  "--color-shadow-scrim": { light: "#17234780", dark: "#000000b3" },
  "--theme-blue-blue-50": { light: "#526AA8", dark: "#8CA4DC" },
  "--theme-blue-blue-60": { light: "#4560A0", dark: "#9BB0E1" },
  "--theme-blue-blue-70": { light: "#3A528E", dark: "#A8BCE8" },
  "--theme-green-green-50": { light: "#3A8F63", dark: "#7FD8B0" },
  "--theme-red-red-50": { light: "#C94A3F", dark: "#F09085" },
  "--theme-yellow-yellow-50": { light: "#C5A468", dark: "#E2CFAA" },
  "--theme-purple-purple-50": { light: "#7A5BB5", dark: "#B8A5E8" },
  "--theme-orange-orange-50": { light: "#D17F33", dark: "#F2A86C" },
  "--theme-cyan-cyan-50": { light: "#2D8FB0", dark: "#6FB8E0" },
  "--theme-magenta-magenta-50": { light: "#B6558C", dark: "#E89AC6" },
}


// QwenWork brand scale: 12 levels (0..100), lightness curve.
const BRAND_LIGHTNESS = [0.96, 0.92, 0.86, 0.78, 0.68, 0.56, 0.44, 0.34, 0.26, 0.20, 0.15, 0.11]
const BRAND_LEVELS = ['0', '5', '10', '20', '30', '40', '50', '60', '70', '80', '90', '100']

// QwenWork neutral scale: 12 levels (0..100).
const NEUTRAL_LIGHTNESS = [0.98, 0.96, 0.92, 0.86, 0.79, 0.70, 0.60, 0.50, 0.40, 0.31, 0.24, 0.18]
const NEUTRAL_LEVELS = ['0', '5', '10', '20', '30', '40', '50', '60', '70', '80', '90', '100']

/** Generate a scale of hex colors from a seed color. */
function generateScale(seedHex: string, lightness: number[]): string[] {
  const seed = hexToRgb(seedHex)
  const [h, s] = rgbToHsl(seed)
  return lightness.map((l) => rgbToHex(hslToRgb(h, s, l)))
}

// QwenWork theme selectors: light group and dark group.
// NOTE: QwenWork's "follow system" mode sets NO data-theme attribute.
// A bare `:not([data-theme])` would force the light palette even when the
// OS is in dark mode — so follow-system is handled via prefers-color-scheme
// media queries below instead.
const LIGHT_SELECTORS = [
  'html[data-skin][data-theme="light"]',
  'html[data-skin][data-theme="light-glass"]',
  'html[data-skin][data-theme="classic-light"]',
  'html[data-skin][data-theme="light-parchment"]',
]
const DARK_SELECTORS = [
  'html[data-skin][data-theme="dark"]',
  'html[data-skin][data-theme="dark-glass"]',
  'html[data-skin][data-theme="classic-dark"]',
  'html[data-skin][data-theme="dark-parchment"]',
]
// Follow-system (no data-theme): resolved via prefers-color-scheme.
const FOLLOW_LIGHT_SELECTOR = 'html[data-skin]:not([data-theme])'
const FOLLOW_DARK_SELECTOR = 'html[data-skin]:not([data-theme])'

/** Build shared scale rules (brand + neutral, independent of theme). */
function buildScaleRules(t: TokenPairs): MappingRule[] {
  const s = t.light
  const brand = generateScale(s['brand-600'], BRAND_LIGHTNESS)
  // Neutral scale carries the brand hue at a noticeable saturation — pure grey
  // reads dead alongside the brand-tinted semantic surfaces/borders.
  // Use ~30% of the brand saturation so the tint reads as thematic, not accidental.
  const [brandHue, brandSat] = rgbToHsl(hexToRgb(s['brand-600']))
  const neutralSat = brandSat * 0.30
  const neutralTinted = NEUTRAL_LIGHTNESS.map((l) =>
    rgbToHex(hslToRgb(brandHue, neutralSat, l)),
  )

  const rules: MappingRule[] = []

  BRAND_LEVELS.forEach((level, i) => {
    rules.push({ target: `--brand-brand-${level}`, source: () => brand[i] })
  })
  NEUTRAL_LEVELS.forEach((level, i) => {
    rules.push({ target: `--theme-neutral-neutral-${level}`, source: () => neutralTinted[i] })
  })

  return rules
}

/** Build semantic rules for a given theme mode.
 *  Consumes the pre-generated light/dark token pair directly — no
 *  per-channel slot tables or brightness hacks needed. */
function buildSemanticRules(t: TokenPairs, mode: ThemeMode): MappingRule[] {
  const set = mode === 'light' ? t.light : t.dark
  const S = (key: keyof Tokens) => set[key]
  // Semantic brand levels — accent/link text must reach ≥4.5:1 on the
  // theme's surface, button backgrounds need white text ≥4.5:1.
  // light: brand-600 works for both. dark: links use brighter brand-500,
  // buttons use deeper brand-700 (white on brand-600 is only 3.73:1).
  const brandLink = mode === 'light' ? S('brand-600') : S('brand-500')
  const brandLinkHover = mode === 'light' ? S('brand-700') : S('brand-300')
  const brandButton = mode === 'light' ? S('brand-600') : S('brand-700')
  const rules: MappingRule[] = []

  // ── Semantic → --status-* (tokens already tuned per theme) ──
  rules.push(
    { target: '--status-success', source: () => S('success-base') },
    { target: '--status-fill-success-container', source: () => S('success-weak') },
    { target: '--status-fill-success-accent', source: () => S('success-strong') },
    { target: '--status-text-success', source: () => S('success-strong') },
    { target: '--status-warning', source: () => S('warning-base') },
    { target: '--status-fill-warning-container', source: () => S('warning-weak') },
    { target: '--status-fill-warning-accent', source: () => S('warning-strong') },
    { target: '--status-text-warning', source: () => S('warning-strong') },
    { target: '--status-error', source: () => S('critical-base') },
    { target: '--status-fill-error-container', source: () => S('critical-weak') },
    { target: '--status-fill-error-accent', source: () => S('critical-strong') },
    { target: '--status-text-error', source: () => S('critical-strong') },
    { target: '--status-link', source: () => S('info-base') },
    { target: '--status-fill-info-container', source: () => S('info-weak') },
    { target: '--status-fill-info-accent', source: () => S('info-strong') },
    { target: '--status-text-info-link', source: () => S('info-base') },
    { target: '--status-text-info-link-hover', source: () => S('info-strong') },
    { target: '--status-text-info-link-pressed', source: () => S('info-strong') },
  )

  // ── Background → --bg-* ──
  rules.push(
    { target: '--bg-base-normal', source: () => S('surface-base') },
    { target: '--bg-base-soft', source: () => S('surface-raised') },
    { target: '--bg-base-strong', source: () => S('surface-strong') },
    { target: '--bg-sidebar-normal', source: () => S('surface-weak') },
    { target: '--bg-card-z0', source: () => S('surface-base') },
    { target: '--bg-card-z1', source: () => S('surface-raised') },
    { target: '--bg-pop', source: () => S('surface-raised') },
    { target: '--bg-tooltips', source: () => S('surface-raised') },
    { target: '--bg-page-mask', source: () => S('surface-strong') },
    { target: '--bg-page-blur-mask', source: () => S('surface-strong') },
  )

  // ── Text → --text-* ──
  rules.push(
    { target: '--text-base-primary', source: () => S('text-strong') },
    { target: '--text-base-secondary', source: () => S('text-base') },
    { target: '--text-base-tertiary', source: () => S('text-weak') },
    { target: '--text-base-disable', source: () => S('text-weaker') },
    // inverted 文字恒为浅色（放在深色/强调表面上），不跟随主题翻转
    { target: '--text-inverted-primary', source: () => t.light['text-inverse'] },
    { target: '--text-inverted-primary-bg', source: () => t.light['text-inverse'] },
    { target: '--text-inverted-secondary', source: () => t.light['text-inverse'] },
    { target: '--text-inverted-tertiary', source: () => t.light['text-inverse'] },
    { target: '--text-inverted-disable', source: () => S('text-weaker') },
    { target: '--text-accent-primary', source: () => brandLink },
    { target: '--text-accent-normal', source: () => brandLink },
    { target: '--text-accent-hover', source: () => brandLinkHover },
    { target: '--text-accent-pressed', source: () => brandLinkHover },
  )

  // ── Border → --border-* ──
  rules.push(
    { target: '--border-theme', source: () => S('border-base') },
    { target: '--border-theme-alpha', source: () => S('border-base') },
    { target: '--border-theme-light', source: () => S('border-weak') },
    { target: '--border-light', source: () => S('border-weak') },
    { target: '--border-medium', source: () => S('border-strong') },
    { target: '--border-strong', source: () => S('border-strong') },
    { target: '--border-focus', source: () => S('border-focus') },
    { target: '--border-accent', source: () => S('brand-600') },
    { target: '--border-checkbox', source: () => S('brand-600') },
    { target: '--border-divider-light', source: () => S('border-weak') },
    { target: '--border-divider-medium', source: () => S('border-base') },
    { target: '--border-shadow', source: () => S('border-weak') },
  )

  // ── Color fill → --color-fill-* ──
  rules.push(
    { target: '--color-fill', source: () => S('brand-600') },
    { target: '--color-fill-secondary', source: () => S('brand-500') },
    { target: '--color-fill-tertiary', source: () => S('brand-300') },
    { target: '--color-fill-quaternary', source: () => S('brand-100') },
    { target: '--color-fill-disable', source: () => S('text-weaker') },
  )

  // ── Controls → --control-* ──
  rules.push(
    { target: '--control-core-button-default', source: () => brandButton },
    { target: '--control-core-button-disabled', source: () => S('text-weaker') },
    { target: '--control-ghost-button-default', source: () => brandButton },
    { target: '--control-ghost-button-disabled', source: () => S('text-weaker') },
    { target: '--control-active', source: () => S('brand-600') },
    { target: '--control-active-disabled', source: () => S('text-weaker') },
    { target: '--control-input-bg', source: () => S('input-base') },
    { target: '--control-input-bg-material', source: () => S('input-base') },
    { target: '--control-segmented-bg', source: () => S('surface-weak') },
    { target: '--control-segmented-selected', source: () => S('surface-raised') },
    { target: '--control-switch-bg', source: () => S('brand-600') },
    { target: '--control-switch-knob', source: () => S('text-inverse') },
    { target: '--control-switch-disabled', source: () => S('text-weaker') },
    { target: '--control-checkbox-default', source: () => S('brand-600') },
    { target: '--control-checkbox-disabled', source: () => S('text-weaker') },
  )

  // ── Overlay → --overlay-* ──
  rules.push(
    { target: '--overlay-on-container-hover', source: () => S('brand-100') },
    { target: '--overlay-on-container-pressed', source: () => S('brand-300') },
    { target: '--overlay-on-container-selected', source: () => S('brand-100') },
    { target: '--overlay-on-primary-black-hover', source: () => S('text-base') },
    { target: '--overlay-on-primary-black-pressed', source: () => S('text-weak') },
    { target: '--overlay-on-primary-black-selected', source: () => S('text-base') },
  )

  // ── Accent ──
  rules.push(
    { target: '--color-accent', source: () => S('accent') },
  )

  // ── Component palette (channel-specific, light/dark variants) ──
  for (const [target, { light, dark }] of Object.entries(COMPONENT_PALETTE)) {
    rules.push({ target, source: () => (mode === 'light' ? light : dark) })
  }

  return rules
}

function formatBlock(selectors: string[], rules: MappingRule[], tokens: TokenPairs): string {
  const lines = [`${selectors.join(',\n')} {`]
  for (const rule of rules) {
    lines.push(`  ${rule.target}: ${rule.source(tokens)};`)
  }
  lines.push('}', '')
  return lines.join('\n')
}

export function generateTokenMappingCSS(tokens: TokenPairs): string {
  const scaleRules = buildScaleRules(tokens)
  const lightRules = buildSemanticRules(tokens, 'light')
  const darkRules = buildSemanticRules(tokens, 'dark')

  // 角色展示配置（CSS 值字符串）：build-tokens 从 manifest char-config 写入 tokens.json，
  // skin.css 以 var(--character-*-height) / var(--character-*-position) 消费。
  // offset [x, y]：x = 距边缘（左角色距左、右角色距右），y = 距底；值原样透传进 calc
  const charConfig =
    tokens['char-config'] ??
    ({} as Record<'character-left' | 'character-right', { offset: [string, string]; height: string }>)
  const leftCfg = charConfig['character-left'] ?? DEFAULT_CHAR_CONFIG['character-left']
  const rightCfg = charConfig['character-right'] ?? DEFAULT_CHAR_CONFIG['character-right']
  const charRules: MappingRule[] = [
    { target: '--character-left-height', source: () => `${leftCfg.height}` },
    { target: '--character-right-height', source: () => `${rightCfg.height}` },
    {
      target: '--character-left-position',
      source: () => `${leftCfg.offset[0]} calc(100% - ${leftCfg.offset[1]})`,
    },
    {
      target: '--character-right-position',
      source: () => `calc(100% - ${rightCfg.offset[0]}) calc(100% - ${rightCfg.offset[1]})`,
    },
  ]

  const lines: string[] = [
    '/* Auto-generated by token-mapping.ts — do not edit manually */',
    '/* Maps universal skin-core tokens to QwenWork CSS variables */',
    '',
    '/* Shared brand/neutral scales (independent of theme) */',
  ]
  lines.push(formatBlock(['html[data-skin]'], scaleRules, tokens))
  lines.push('/* Character art config (from manifest char-config, consumed by skin.css) */')
  lines.push(formatBlock(['html[data-skin]'], charRules, tokens))
  lines.push('/* Light theme semantics (explicit light themes) */')
  lines.push(formatBlock(LIGHT_SELECTORS, lightRules, tokens))
  lines.push('/* Dark theme semantics (explicit dark themes) */')
  lines.push(formatBlock(DARK_SELECTORS, darkRules, tokens))

  // Follow-system mode: QwenWork sets no data-theme attribute; the OS
  // color scheme decides. Wrap each palette in a media query.
  lines.push('/* Follow-system (no data-theme): match OS color scheme */')
  lines.push('@media (prefers-color-scheme: light) {')
  lines.push(formatBlock([FOLLOW_LIGHT_SELECTOR], lightRules, tokens).replace(/\n$/, ''))
  lines.push('}')
  lines.push('')
  lines.push('@media (prefers-color-scheme: dark) {')
  lines.push(formatBlock([FOLLOW_DARK_SELECTOR], darkRules, tokens).replace(/\n$/, ''))
  lines.push('}')
  lines.push('')

  return lines.join('\n')
}

// CLI: read tokens.json, write token-mapping.css
if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))
) {
  const tokensPath = path.resolve('../../skin-core/dist/tokens.json')
  const outPath = path.resolve('dist/token-mapping.css')

  if (!fs.existsSync(tokensPath)) {
    console.error(`❌ tokens.json not found at ${tokensPath}. Run 'pnpm build-tokens' first.`)
    process.exit(1)
  }

  const tokens: TokenPairs = JSON.parse(fs.readFileSync(tokensPath, 'utf8'))
  const css = generateTokenMappingCSS(tokens)

  fs.mkdirSync(path.dirname(outPath), { recursive: true })
  fs.writeFileSync(outPath, css)

  const total = buildScaleRules(tokens).length + buildSemanticRules(tokens, 'light').length + buildSemanticRules(tokens, 'dark').length
  console.log(`✓ Generated ${outPath} (${total} mappings)`)
}
