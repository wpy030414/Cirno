// packages/skins/opencode/src/token-mapping.ts
// Map universal tokens to OpenCode's CSS variable system.
// Maintained by the opencode skin author, not skin-core.
// Generates three blocks:
//   html[data-skin]                          — shared color scales (9 hues × 13 levels)
//   html[data-skin][data-color-scheme="light"] — light semantic variables
//   html[data-skin][data-color-scheme="dark"]  — dark semantic variables
import fs from 'node:fs'
import path from 'node:path'
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
export type TokenPairs = {
  light: Tokens
  dark: Tokens
  /** 角色展示配置（offset/height 为 CSS 值字符串），build-tokens 从 manifest char-config 写入 */
  'char-config'?: Record<'character-left' | 'character-right', { offset: [string, string]; height: string }>
}

/** Channel-specific component palette (light/dark variants, not derived from image tokens). */
const COMPONENT_PALETTE: Record<string, { light: string; dark: string }> = {
  "--surface-raised-base-hover": { light: "#F2F6FEDB", dark: "#1E3468F2" },
  "--surface-weaker": { light: "#E3EAF7", dark: "#223A6E" },
  "--surface-interactive-base": { light: "#E3EBFB", dark: "#223A6E" },
  "--surface-interactive-hover": { light: "#D6E2F8", dark: "#2A4580" },
  "--surface-interactive-weak": { light: "#F2F7FE", dark: "#182A5A" },
  "--surface-success-base": { light: "#E6F4E7", dark: "#16251C" },
  "--surface-warning-base": { light: "#F8EEDC", dark: "#2A2414" },
  "--surface-critical-base": { light: "#FBE9E6", dark: "#2E1410" },
  "--surface-info-base": { light: "#E6EDFB", dark: "#12213E" },
  "--input-hover": { light: "#FAF9F4E0", dark: "#15244DE0" },
  "--input-selected": { light: "#D6E2F8", dark: "#223A6E" },
  "--input-disabled": { light: "#E9EDF6", dark: "#1A2545" },
  "--text-interactive-base": { light: "#526AA8", dark: "#8CA4DC" },
  "--text-diff-add-base": { light: "#3A7F5A", dark: "#7FD8B0" },
  "--text-diff-delete-base": { light: "#C94A3F", dark: "#F09085" },
  "--text-diff-add-strong": { light: "#1E4E35", dark: "#B8E9C9" },
  "--text-diff-delete-strong": { light: "#7A241C", dark: "#F7BEB6" },
  "--button-ghost-hover": { light: "#526AA81F", dark: "#A8BCE82E" },
  "--border-hover": { light: "#475B9148", dark: "#97A9D872" },
  "--border-active": { light: "#475B9168", dark: "#97A9D8A0" },
  "--border-selected": { light: "#C5A468A3", dark: "#D3B477A8" },
  "--border-weaker-base": { light: "#CBD6EC", dark: "#263B6B" },
  "--border-interactive-base": { light: "#A7B9E0", dark: "#4A66A5" },
  "--border-interactive-hover": { light: "#8EA5DA", dark: "#6C86C4" },
  "--border-interactive-active": { light: "#526AA8", dark: "#8CA4DC" },
  "--border-interactive-selected": { light: "#C5A468", dark: "#D3B477" },
  "--border-success-base": { light: "#B8E3C2", dark: "#2E4F41" },
  "--border-warning-base": { light: "#E4CB9B", dark: "#6B5528" },
  "--border-critical-base": { light: "#F2C0B8", dark: "#6B2A22" },
  "--border-info-base": { light: "#C3D2F2", dark: "#33518F" },
  "--icon-weak-base": { light: "#8A94AA", dark: "#96A6C9" },
  "--icon-strong-base": { light: "#172347", dark: "#E5EAF6" },
  "--icon-success-base": { light: "#3A8F63", dark: "#7FD8B0" },
  "--icon-warning-base": { light: "#C5A468", dark: "#E2CFAA" },
  "--icon-critical-base": { light: "#C94A3F", dark: "#F09085" },
  "--icon-info-base": { light: "#526AA8", dark: "#8CA4DC" },
  "--icon-on-brand-base": { light: "#FFFFFF", dark: "#0B1633" },
  "--icon-agent-plan-base": { light: "#7A5BB5", dark: "#C9A4E8" },
  "--icon-agent-docs-base": { light: "#D17F33", dark: "#E8B877" },
  "--icon-agent-ask-base": { light: "#2D8FB0", dark: "#6FB8E0" },
  "--icon-agent-build-base": { light: "#526AA8", dark: "#8CA4DC" },
  "--syntax-regexp": { light: "#3C4C73", dark: "#BDC9E3" },
  "--syntax-primitive": { light: "#C05B7D", dark: "#F0A0BD" },
  "--syntax-property": { light: "#9A6B2F", dark: "#E2CFAA" },
  "--syntax-type": { light: "#5B6FB8", dark: "#A5B8E8" },
  "--syntax-punctuation": { light: "#6F7C99", dark: "#96A6C9" },
  "--syntax-object": { light: "#172347", dark: "#E5EAF6" },
  "--syntax-diff-unknown": { light: "#7A5BB5", dark: "#B8A5E8" },
  "--markdown-link-text": { light: "#6A7FB0", dark: "#A8BCE8" },
  "--markdown-emph": { light: "#B38745", dark: "#E2CFAA" },
  "--markdown-list-enumeration": { light: "#6A7FB0", dark: "#A8BCE8" },
  "--markdown-image": { light: "#526AA8", dark: "#9BB0E1" },
  "--markdown-image-text": { light: "#6A7FB0", dark: "#A8BCE8" },
  "--markdown-code-block": { light: "#172347", dark: "#E5EAF6" },
  "--surface-diff-add-base": { light: "#E6F4E7", dark: "#16251C" },
  "--surface-diff-add-weak": { light: "#F1F9F2", dark: "#1B2E22" },
  "--surface-diff-add-strong": { light: "#BFE4C4", dark: "#23402F" },
  "--surface-diff-add-stronger": { light: "#8FD0A0", dark: "#4E9E72" },
  "--surface-diff-delete-base": { light: "#FBE9E6", dark: "#2E1410" },
  "--surface-diff-delete-weak": { light: "#FDF4F2", dark: "#3A1A15" },
  "--surface-diff-delete-strong": { light: "#F5C9C2", dark: "#4A1D16" },
  "--surface-diff-delete-stronger": { light: "#E89A90", dark: "#D97B6D" },
  "--surface-diff-hidden-base": { light: "#E4EDFB", dark: "#0C1928" },
  "--surface-diff-hidden-weak": { light: "#F0F5FD", dark: "#0D1D33" },
  "--surface-diff-hidden-strong": { light: "#C4D8F5", dark: "#123456" },
  "--surface-diff-hidden-stronger": { light: "#7FA4DD", dark: "#3A6EA8" },
  "--avatar-background-pink": { light: "#FDEFF6", dark: "#3F2440" },
  "--avatar-background-mint": { light: "#E2F8F2", dark: "#0E3A34" },
  "--avatar-background-orange": { light: "#FFF1E4", dark: "#3F2A12" },
  "--avatar-background-purple": { light: "#F3ECFC", dark: "#2D2350" },
  "--avatar-background-cyan": { light: "#E4F5FC", dark: "#12324A" },
  "--avatar-background-lime": { light: "#F0F7E0", dark: "#2B3A1C" },
  "--avatar-text-pink": { light: "#B6558C", dark: "#E89AC6" },
  "--avatar-text-mint": { light: "#2F8F77", dark: "#7FD8BC" },
  "--avatar-text-orange": { light: "#D17F33", dark: "#F2A86C" },
  "--avatar-text-purple": { light: "#7A5BB5", dark: "#B3A4EC" },
  "--avatar-text-cyan": { light: "#2D8FB0", dark: "#6FB8E0" },
  "--avatar-text-lime": { light: "#6D8A2F", dark: "#B4D878" },
}


// OpenCode v2 scales: lightness curve for levels 50/100...1200.
// (50 is pure white; 100..1200 follow this curve.)
const LIGHTNESS_100_1200 = [0.96, 0.92, 0.86, 0.79, 0.68, 0.54, 0.42, 0.30, 0.24, 0.19, 0.15, 0.11]
const LEVELS = ['100', '200', '300', '400', '500', '600', '700', '800', '900', '1000', '1100', '1200']

/** Generate a 12-level v2 scale (#RRGGBBFF) from a seed hex color. */
function scale12(seedHex: string): string[] {
  const seed = hexToRgb(seedHex)
  const [h, s] = rgbToHsl(seed)
  return LIGHTNESS_100_1200.map((l) => rgbToHex(hslToRgb(h, s, l)))
}

/** 全 100% 不透明：token 管线禁止透明度通道，直接返回 6 位 hex。 */
const withAlpha = (hex: string) => hex

/** Build shared color scale rules (independent of light/dark). */
function buildScaleRules(t: TokenPairs): MappingRule[] {
  // Scales derive from the light set — neutral/brand scales are identical
  // in both palettes (same clustering → same scales).
  const s = t.light
  const grey = scale12(s['neutral-500'])
  const blue = scale12(s['brand-600'])
  const green = scale12(s['success-base'])
  const yellow = scale12(s['warning-base'])
  const red = scale12(s['critical-base'])
  const cyan = scale12(s['info-base'])
  const purple = scale12(s['accent'])
  const pink = scale12(s['accent'])
  const orange = scale12(s['warning-base'])

  const rules: MappingRule[] = []

  // Grey scale (13 levels)
  rules.push({ target: '--v2-grey-50', source: () => '#FFFFFF' })
  LEVELS.forEach((level, i) => {
    rules.push({ target: `--v2-grey-${level}`, source: () => grey[i] })
  })

  // Color scales (12 levels each)
  const scales: Array<[string, string[]]> = [
    ['red', red], ['orange', orange], ['yellow', yellow], ['green', green],
    ['cyan', cyan], ['blue', blue], ['purple', purple], ['pink', pink],
  ]
  for (const [hue, scale] of scales) {
    LEVELS.forEach((level, i) => {
      rules.push({ target: `--v2-${hue}-${level}`, source: () => scale[i] })
    })
  }

  return rules
}

/** Build semantic rules for a given theme mode.
 *  Consumes the pre-generated light/dark token pair directly — no
 *  per-channel slot tables or brightness hacks needed. */
function buildSemanticRules(t: TokenPairs, mode: ThemeMode): MappingRule[] {
  const set = mode === 'light' ? t.light : t.dark
  const S = (key: keyof Tokens) => set[key]
  // Semantic brand levels — link/emphasis text must reach ≥4.5:1 on the
  // theme's surface, while button backgrounds need white text ≥4.5:1.
  // light: brand-600 works for both. dark: links use brighter brand-500,
  // buttons use deeper brand-700 (white on brand-600 is only 3.73:1).
  const brandLink = mode === 'light' ? S('brand-600') : S('brand-500')
  const brandLinkHover = mode === 'light' ? S('brand-700') : S('brand-300')
  const brandButton = mode === 'light' ? S('brand-600') : S('brand-700')
  const rules: MappingRule[] = []

  // ── Background layers ──
  rules.push(
    { target: '--v2-background-bg-base', source: () => S('surface-base') },
    { target: '--v2-background-bg-deep', source: () => S('surface-weak') },
    { target: '--v2-background-bg-layer-01', source: () => S('surface-base') },
    { target: '--v2-background-bg-layer-02', source: () => S('surface-weak') },
    { target: '--v2-background-bg-layer-03', source: () => S('neutral-300') },
    { target: '--v2-background-bg-layer-04', source: () => S('neutral-400') },
    { target: '--v2-background-bg-inverse', source: () => S('surface-strong') },
    { target: '--v2-background-bg-contrast', source: () => S('text-strong') },
    { target: '--v2-background-bg-button-neutral', source: () => S('surface-raised') },
    { target: '--v2-background-bg-accent', source: () => S('brand-700') },
  )

  // ── Text ──
  rules.push(
    { target: '--v2-text-text-base', source: () => S('text-base') },
    { target: '--v2-text-text-muted', source: () => S('text-weak') },
    { target: '--v2-text-text-faint', source: () => S('text-weaker') },
    // inverse = on a dark accent surface (buttons, selected tabs) in BOTH
    // themes — always a light tint, never the theme-dependent text-inverse.
    { target: '--v2-text-text-inverse', source: () => `var(--v2-grey-50)` },
    { target: '--v2-text-text-contrast', source: () => S('text-inverse') },
    { target: '--v2-text-text-accent', source: () => brandLink },
    { target: '--v2-text-text-accent-hover', source: () => brandLinkHover },
    { target: '--v2-text-text-code-accent', source: () => S('success-base') },
  )

  // ── Icon ──
  rules.push(
    { target: '--v2-icon-icon-base', source: () => S('text-weak') },
    { target: '--v2-icon-icon-muted', source: () => S('text-weaker') },
    // inverse icons sit on dark accent surfaces in both themes → always light
    { target: '--v2-icon-icon-inverse', source: () => `var(--v2-grey-50)` },
    { target: '--v2-icon-icon-contrast', source: () => S('text-inverse') },
    { target: '--v2-icon-icon-accent', source: () => brandLink },
    { target: '--v2-icon-icon-accent-hover', source: () => brandLinkHover },
  )

  // ── Border ──
  rules.push(
    { target: '--v2-border-border-base', source: () => S('border-base') },
    { target: '--v2-border-border-muted', source: () => S('border-weak') },
    { target: '--v2-border-border-strong', source: () => S('border-strong') },
    { target: '--v2-border-border-focus', source: () => S('border-focus') },
    { target: '--v2-border-border-inverse', source: () => S('surface-strong') },
  )

  // ── State (semantic) — tokens already tuned per theme ──
  const state = (hue: 'success' | 'warning' | 'critical' | 'info') => ({
    fg: S(`${hue}-strong` as keyof Tokens),
    bg: S(`${hue}-weak` as keyof Tokens),
    border: S(`${hue}-base` as keyof Tokens),
  })
  rules.push(
    { target: '--v2-state-fg-success', source: () => state('success').fg },
    { target: '--v2-state-fg-warning', source: () => state('warning').fg },
    { target: '--v2-state-fg-danger', source: () => state('critical').fg },
    { target: '--v2-state-fg-info', source: () => state('info').fg },
    { target: '--v2-state-bg-success', source: () => state('success').bg },
    { target: '--v2-state-bg-warning', source: () => state('warning').bg },
    { target: '--v2-state-bg-danger', source: () => state('critical').bg },
    { target: '--v2-state-bg-info', source: () => state('info').bg },
    { target: '--v2-state-border-success', source: () => state('success').border },
    { target: '--v2-state-border-warning', source: () => state('warning').border },
    { target: '--v2-state-border-danger', source: () => state('critical').border },
    { target: '--v2-state-border-info', source: () => state('info').border },
  )

  // ── Overlay (scrim follows the theme's strong surface) ──
  rules.push(
    { target: '--v2-overlay-simple-overlay-scrim', source: () => S('surface-strong') },
    { target: '--v2-overlay-simple-overlay-hover', source: () => S('surface-strong') },
    { target: '--v2-overlay-simple-overlay-pressed', source: () => S('surface-strong') },
    { target: '--v2-overlay-simple-tab-scrim', source: () => S('surface-strong') },
    { target: '--v2-overlay-simple-tab-hover-scrim', source: () => S('surface-strong') },
    { target: '--v2-overlay-simple-tab-active-scrim', source: () => S('surface-strong') },
  )

  // ── Avatars (fixed hues, derived from color scales) ──
  const avatarHues = ['red', 'orange', 'yellow', 'green', 'cyan', 'blue', 'purple', 'pink']
  for (const hue of avatarHues) {
    rules.push(
      { target: `--v2-avatar-bg-${hue}`, source: () => `var(--v2-${hue}-100)` },
      { target: `--v2-avatar-border-${hue}`, source: () => `var(--v2-${hue}-300)` },
    )
  }
  rules.push(
    { target: '--v2-avatar-bg-gray', source: () => S('surface-weak') },
    { target: '--v2-avatar-border-gray', source: () => S('border-weak') },
  )

  // ── Legacy semantic aliases (used by older skin.css rules) ──
  rules.push(
    { target: '--background-base', source: () => S('surface-base') },
    { target: '--background-weak', source: () => S('surface-weak') },
    { target: '--background-strong', source: () => S('neutral-300') },
    { target: '--background-stronger', source: () => S('neutral-400') },
    { target: '--surface-base', source: () => S('surface-base') },
    { target: '--surface-raised-base', source: () => S('surface-raised') },
    { target: '--surface-strong', source: () => S('surface-strong') },
    { target: '--surface-weak', source: () => S('surface-weak') },
    { target: '--text-strong', source: () => S('text-strong') },
    { target: '--text-base', source: () => S('text-base') },
    { target: '--text-weak', source: () => S('text-weak') },
    { target: '--text-weaker', source: () => S('text-weaker') },
    { target: '--text-invert-base', source: () => S('text-inverse') },
    { target: '--text-invert-strong', source: () => S('text-inverse') },
    { target: '--border-base', source: () => S('border-base') },
    { target: '--border-weak-base', source: () => S('border-weak') },
    { target: '--border-strong-base', source: () => S('border-strong') },
    { target: '--border-focus', source: () => S('border-focus') },
    { target: '--button-primary-base', source: () => brandButton },
    { target: '--button-secondary-base', source: () => S('surface-raised') },
    { target: '--input-base', source: () => S('input-base') },
    { target: '--input-active', source: () => S('input-active') },
    { target: '--icon-base', source: () => S('text-weak') },
    { target: '--icon-hover', source: () => brandLink },
    { target: '--icon-active', source: () => S('text-strong') },
    { target: '--icon-selected', source: () => S('text-strong') },
    { target: '--icon-disabled', source: () => S('text-weaker') },
    { target: '--icon-interactive-base', source: () => brandLink },
    { target: '--syntax-comment', source: () => S('text-weak') },
    { target: '--syntax-string', source: () => S('success-base') },
    { target: '--syntax-keyword', source: () => brandLink },
    { target: '--syntax-operator', source: () => S('text-base') },
    { target: '--syntax-variable', source: () => S('text-strong') },
    { target: '--syntax-constant', source: () => S('accent') },
    { target: '--syntax-critical', source: () => S('critical-base') },
    { target: '--syntax-success', source: () => S('success-base') },
    { target: '--syntax-warning', source: () => S('warning-base') },
    { target: '--syntax-info', source: () => S('info-base') },
    { target: '--syntax-diff-add', source: () => S('success-base') },
    { target: '--syntax-diff-delete', source: () => S('critical-base') },
    { target: '--markdown-heading', source: () => S('brand-700') },
    { target: '--markdown-text', source: () => S('text-strong') },
    { target: '--markdown-link', source: () => brandLink },
    { target: '--markdown-code', source: () => S('success-base') },
    { target: '--markdown-block-quote', source: () => S('text-weak') },
    { target: '--markdown-strong', source: () => S('text-strong') },
    { target: '--markdown-horizontal-rule', source: () => S('border-weak') },
    { target: '--markdown-list-item', source: () => brandLink },
  )

  // ── Component palette (channel-specific, light/dark variants) ──
  for (const [target, { light, dark }] of Object.entries(COMPONENT_PALETTE)) {
    rules.push({ target, source: () => (mode === 'light' ? light : dark) })
  }

  return rules
}

function formatBlock(selector: string, rules: MappingRule[], tokens: TokenPairs): string {
  const lines = [`${selector} {`]
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
    '/* Maps universal skin-core tokens (light+dark pair) to OpenCode CSS variables */',
    '',
    '/* Shared color scales (independent of light/dark) */',
  ]
  lines.push(formatBlock('html[data-skin]', scaleRules, tokens))
  lines.push('/* Character art config (from manifest char-config, consumed by skin.css) */')
  lines.push(formatBlock('html[data-skin]', charRules, tokens))
  lines.push('/* Light theme semantics */')
  lines.push(formatBlock('html[data-skin][data-color-scheme="light"]', lightRules, tokens))
  lines.push('/* Dark theme semantics */')
  lines.push(formatBlock('html[data-skin][data-color-scheme="dark"]', darkRules, tokens))

  return lines.join('\n')
}

// CLI: read tokens.json (light+dark pair), write token-mapping.css
if (import.meta.url === `file://${process.argv[1]}`) {
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
