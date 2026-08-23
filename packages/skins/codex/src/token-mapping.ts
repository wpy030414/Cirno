// src/token-mapping.ts
// Codex Desktop token 映射——唯一颜色权威：
//   - dist/token-mapping.css ：CDP 注入层（主通道）。46 tokens → Codex webview 的 --color-token-* 设计变量
//   - dist/token-mapping.toml ：config.toml 原生 chrome 层（辅通道）。[desktop.appearance*ChromeTheme] 颜色键
//   - dist/codex-theme-v1-{light,dark}.txt ：官方 Settings → Appearance → Import 导入串（云持久化，手动导入）
// 颜色全部来自 tokens.json（light/dark 双套），本文件不硬编码颜色；
// 带 alpha 的 8 位 hex 仅出现在 COMPONENT_PALETTE（组件表，与 opencode 同性质）。
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { DEFAULT_CHAR_CONFIG } from '@skins/core/assets-loader'
import type { Tokens } from '@skins/core'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

export interface TokenPairs {
  light: Tokens
  dark: Tokens
  'char-config'?: Record<'character-left' | 'character-right', { offset: [string, string]; height: string }>
}

export type ThemeMode = 'light' | 'dark'

export interface ChromeThemeColors {
  accent: string
  ink: string
  surface: string
  diffAdded: string
  diffRemoved: string
  skill: string
}

const S = (t: TokenPairs, mode: ThemeMode, key: keyof Tokens): string => t[mode][key]

/** 6 位 hex → 8 位带 alpha hex（组件表专属）。非 6 位 hex 原样返回。 */
export function withAlpha(hex: string, alpha: number): string {
  const a = Math.round(Math.min(1, Math.max(0, alpha)) * 255).toString(16).padStart(2, '0')
  const m = hex.match(/^#([0-9a-fA-F]{6})$/)
  if (!m) return hex
  return '#' + m[1] + a
}

/** WCAG 2.x 对比度（测试与 skill 兜底共用） */
export function luminance(hex: string): number {
  const m = hex.match(/^#([0-9a-fA-F]{6})/)
  if (!m) throw new Error('not a 6-digit hex: ' + hex)
  const [r, g, b] = [0, 2, 4].map((i) => {
    const c = parseInt(m[1].slice(i, i + 2), 16) / 255
    return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)
  })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

export function contrastRatio(a: string, b: string): number {
  const l1 = luminance(a)
  const l2 = luminance(b)
  return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05)
}

// ── 语义分档（DECISIONS D-018 同原则：链接亮档 / 按钮深档 / inverted 恒浅） ──
const brandLink = (t: TokenPairs, mode: ThemeMode): string =>
  mode === 'light' ? S(t, mode, 'brand-600') : S(t, mode, 'brand-500')

type Rule = { target: string; source: (t: TokenPairs, mode: ThemeMode) => string }

// ── 共享色阶（图表色，从 light 集推导，两模式一致） ──
function buildSharedRules(t: TokenPairs): Rule[] {
  const L = (key: keyof Tokens) => S(t, 'light', key)
  return [
    { target: '--color-token-charts-blue', source: () => L('info-base') },
    { target: '--color-token-charts-green', source: () => L('success-base') },
    { target: '--color-token-charts-orange', source: () => L('warning-base') },
    { target: '--color-token-charts-purple', source: () => L('accent') },
    { target: '--color-token-charts-red', source: () => L('critical-base') },
    { target: '--color-token-charts-yellow', source: () => L('warning-base') },
  ]
}

// ── 语义规则（light/dark 各自一套） ──
function buildSemanticRules(t: TokenPairs, mode: ThemeMode): Rule[] {
  const A = (key: keyof Tokens, alpha: number) => withAlpha(S(t, mode, key), alpha)
  const rules: Rule[] = []
  const add = (target: string, source: Rule['source']) => rules.push({ target, source })

  // ── 表面层 ──
  add('--color-token-bg-primary', () => S(t, mode, 'surface-base'))
  add('--color-token-bg-secondary', () => S(t, mode, 'surface-raised'))
  add('--color-token-bg-tertiary', () => S(t, mode, 'surface-weak'))
  add('--color-token-main-surface-primary', () => S(t, mode, 'surface-base'))
  add('--color-token-side-bar-background', () => mode === 'dark' ? S(t, mode, 'neutral-1100') : S(t, mode, 'neutral-100'))
  add('--color-token-editor-background', () => S(t, mode, 'surface-raised'))
  add('--color-token-dropdown-background', () => S(t, mode, 'surface-raised'))
  add('--color-token-menu-background', () => S(t, mode, 'surface-raised'))
  add('--color-token-application-menu-background', () => mode === 'dark' ? S(t, mode, 'neutral-800') : S(t, mode, 'surface-weak'))
  add('--color-token-button-background', () => S(t, mode, 'surface-raised'))
  add('--color-token-input-background', () => S(t, mode, 'input-base'))
  add('--color-token-discount-badge-background', () => S(t, mode, 'success-strong'))
  add('--color-token-diff-surface', () => S(t, mode, 'surface-weak'))
  add('--color-token-bg-appshot', () => A('surface-base', 0.75))

  // ── 文字层 ──
  add('--color-token-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-description-foreground', () => S(t, mode, 'text-weak'))
  add('--color-token-disabled-foreground', () => S(t, mode, 'text-weaker'))
  add('--color-token-input-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-input-placeholder-foreground', () => S(t, mode, 'text-weak'))
  add('--color-token-icon-foreground', () => S(t, mode, 'text-base'))
  add('--color-token-editor-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-application-menu-foreground', () => S(t, mode, 'text-base'))
  add('--color-token-dropdown-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-button-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-radio-active-foreground', () => S(t, mode, 'text-inverse'))
  add('--color-token-application-menu-separator', () => A('text-base', 0.37))
  add('--color-token-badge-foreground', () => A('text-base', 0.71))
  add('--color-token-conversation-body', () => A('text-base', 0.60))
  add('--color-token-conversation-header', () => A('text-base', 0.30))
  add('--color-token-conversation-summary-leading', () => A('text-base', 0.50))
  add('--color-token-conversation-summary-trailing', () => A('text-base', 0.40))
  add('--color-token-non-assistant-body-descendant', () => A('text-base', 0.50))

  // ── 强调/链接/选区 ──
  add('--color-token-primary', () => brandLink(t, mode))
  add('--color-token-link', () => brandLink(t, mode))
  add('--color-token-on-accent', () => '#FFFFFF')
  add('--color-token-focus-border', () => withAlpha(brandLink(t, mode), 0.76))
  add('--color-token-list-focus-outline', () => withAlpha(brandLink(t, mode), 0.76))
  add('--color-token-image-remove-brush', () => S(t, mode, 'brand-500'))
  add('--color-token-list-active-selection-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-list-active-selection-icon-foreground', () => S(t, mode, 'text-base'))
  add('--color-token-menubar-selection-foreground', () => S(t, mode, 'text-strong'))

  // ── 边框 ──
  add('--color-token-border', () => S(t, mode, 'border-base'))
  add('--color-token-border-default', () => S(t, mode, 'border-base'))
  add('--color-token-border-heavy', () => S(t, mode, 'border-strong'))
  add('--color-token-border-light', () => S(t, mode, 'border-weak'))
  add('--color-token-button-border', () => S(t, mode, 'border-base'))
  add('--color-token-checkbox-border', () => S(t, mode, 'border-base'))
  add('--color-token-checkbox-foreground', () => S(t, mode, 'text-strong'))
  add('--color-token-input-border', () => S(t, mode, 'border-strong'))
  add('--color-token-menu-border', () => S(t, mode, 'border-base'))
  add('--color-token-radio-inactive-border', () => S(t, mode, 'border-strong'))

  // ── 语义状态 ──
  add('--color-token-error-foreground', () => S(t, mode, 'critical-base'))
  add('--color-token-editor-error-foreground', () => S(t, mode, 'critical-base'))
  add('--color-token-editor-warning-foreground', () => S(t, mode, 'warning-base'))
  add('--color-token-git-decoration-added-resource-foreground', () => S(t, mode, 'success-base'))
  add('--color-token-git-decoration-untracked-resource-foreground', () => S(t, mode, 'success-base'))
  add('--color-token-git-decoration-deleted-resource-foreground', () => S(t, mode, 'critical-base'))
  add('--color-token-git-decoration-modified-resource-foreground', () => S(t, mode, 'warning-base'))
  add('--color-token-git-decoration-renamed-resource-foreground', () => S(t, mode, 'warning-base'))
  add('--color-token-git-decoration-ignored-resource-foreground', () => S(t, mode, 'warning-base'))
  add('--color-token-diff-editor-inserted-line-background', () => A('success-base', 0.23))
  add('--color-token-diff-editor-removed-line-background', () => A('critical-base', 0.23))
  add('--color-token-diff-editor-removed-text-background', () => A('critical-base', 0.23))
  add('--color-token-input-validation-error-background', () => S(t, mode, 'critical-weak'))
  add('--color-token-input-validation-warning-background', () => S(t, mode, 'warning-weak'))
  add('--color-token-input-validation-info-background', () => S(t, mode, 'info-weak'))
  add('--color-token-input-validation-error-border', () => A('critical-base', 0.40))
  add('--color-token-input-validation-warning-border', () => A('warning-base', 0.40))
  add('--color-token-editor-group-drop-background', () => S(t, mode, 'info-weak'))
  add('--color-token-editor-group-drop-into-prompt-background', () => S(t, mode, 'info-weak'))
  add('--color-token-editor-group-drop-into-prompt-foreground', () => S(t, mode, 'text-inverse'))
  add('--color-token-discount-badge-foreground', () => '#FFFFFF')

  // ── 白板/工作区（oai-wb 家族）与专项表面/文字/边框 ──
  add('--oai-wb-surface-primary', () => S(t, mode, 'surface-raised'))
  add('--oai-wb-surface-secondary', () => S(t, mode, 'surface-base'))
  add('--oai-wb-text-primary', () => S(t, mode, 'text-strong'))
  add('--oai-wb-text-secondary', () => S(t, mode, 'text-base'))
  add('--oai-wb-text-tertiary', () => S(t, mode, 'text-weaker'))
  add('--oai-wb-accent', () => brandLink(t, mode))
  add('--oai-wb-entity-accent', () => S(t, mode, 'accent'))
  add('--oai-wb-control-border', () => S(t, mode, 'border-base'))
  add('--oai-wb-on-accent', () => '#FFFFFF')
  add('--main-surface-secondary', () => S(t, mode, 'surface-raised'))
  add('--text-primary', () => S(t, mode, 'text-strong'))
  add('--border-medium', () => S(t, mode, 'border-base'))
  add('--border-light', () => S(t, mode, 'border-weak'))
  add('--color-token-text-link-foreground', () => brandLink(t, mode))
  add('--mermaid-surface-background', () => S(t, mode, 'surface-raised'))
  add('--referral-rate-limit-surface', () => S(t, mode, 'surface-raised'))
  add('--referral-rate-limit-muted-surface', () => S(t, mode, 'surface-weak'))
  add('--referral-rate-limit-input-background', () => S(t, mode, 'input-base'))
  add('--referral-rate-limit-border', () => S(t, mode, 'border-base'))
  add('--referral-rate-limit-check-background', () => S(t, mode, 'success-base'))
  add('--referral-rate-limit-success-background', () => S(t, mode, 'success-weak'))
  add('--color-token-activity-bar-badge-foreground', () => '#FFFFFF')
  return rules
}

// ── 组件表：渠道专属的 alpha/层级选择（色相来自 tokens，档位参照 Codex 原生透明度体系） ──
function buildComponentRules(t: TokenPairs, mode: ThemeMode): Rule[] {
  const rules: Rule[] = []
  const add = (target: string, source: Rule['source']) => rules.push({ target, source })
  const modeValue = (light: string, dark: string) => (mode === 'light' ? light : dark)
  add('--color-token-checkbox-background', () => withAlpha(S(t, mode, 'surface-raised'), 0.96))
  add('--color-token-editor-widget-background', () => withAlpha(S(t, mode, 'surface-raised'), 0.96))
  add('--color-token-editor-selection-background', () => modeValue(withAlpha(S(t, 'light', 'brand-100'), 0.90), withAlpha(S(t, 'dark', 'brand-900'), 0.70)))
  add('--color-token-editor-find-match-background', () => modeValue(withAlpha(S(t, 'light', 'info-base'), 0.28), withAlpha(S(t, 'dark', 'info-base'), 0.34)))
  add('--color-token-editor-find-match-highlight-background', () => modeValue(withAlpha(S(t, 'light', 'info-base'), 0.44), withAlpha(S(t, 'dark', 'info-base'), 0.52)))
  add('--color-token-progress-bar-background', () => modeValue(withAlpha(S(t, 'light', 'brand-300'), 0.45), withAlpha(S(t, 'dark', 'brand-700'), 0.60)))
  add('--color-token-activity-bar-badge-background', () => modeValue(S(t, 'light', 'brand-700'), S(t, 'dark', 'brand-600')))
  add('--color-token-list-active-selection-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.10), withAlpha(S(t, 'dark', 'neutral-200'), 0.06)))
  add('--color-token-list-hover-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.08), withAlpha(S(t, 'dark', 'neutral-200'), 0.08)))
  add('--color-token-menubar-selection-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.06), withAlpha(S(t, 'dark', 'neutral-200'), 0.04)))
  add('--color-token-badge-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.08), withAlpha(S(t, 'dark', 'neutral-200'), 0.06)))
  add('--color-token-scrollbar-slider-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.20), withAlpha(S(t, 'dark', 'neutral-200'), 0.20)))
  add('--color-token-scrollbar-slider-hover-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.30), withAlpha(S(t, 'dark', 'neutral-200'), 0.30)))
  add('--color-token-scrollbar-slider-active-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.40), withAlpha(S(t, 'dark', 'neutral-200'), 0.40)))
  add('--color-token-bg-fog', () => modeValue(withAlpha(S(t, 'light', 'text-base'), 0.04), withAlpha(S(t, 'dark', 'text-base'), 0.03)))
  add('--oai-wb-interactive-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.08), withAlpha(S(t, 'dark', 'neutral-200'), 0.08)))
  add('--composer-top-tray-background', () => withAlpha(S(t, mode, 'surface-raised'), 0.85))
  add('--vscode-terminal-background', () => modeValue(S(t, 'light', 'surface-raised'), S(t, 'dark', 'surface-base')))
  add('--color-token-text-code-block-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.08), withAlpha(S(t, 'dark', 'neutral-200'), 0.06)))
  add('--color-token-button-secondary-hover-background', () => modeValue(withAlpha(S(t, 'light', 'neutral-500'), 0.08), withAlpha(S(t, 'dark', 'neutral-200'), 0.08)))
  add('--shimmer-text-secondary', () => withAlpha(S(t, mode, 'text-base'), 0.60))
  add('--referral-rate-limit-tracking-background', () => modeValue(withAlpha(S(t, 'light', 'brand-600'), 0.16), withAlpha(S(t, 'dark', 'brand-500'), 0.16)))
  add('--referral-rate-limit-tracking-hover-background', () => modeValue(withAlpha(S(t, 'light', 'brand-600'), 0.26), withAlpha(S(t, 'dark', 'brand-500'), 0.26)))
  return rules
}

function formatBlock(selector: string, rules: Rule[], t: TokenPairs, mode: ThemeMode): string {
  const lines = [selector + ' {']
  for (const rule of rules) lines.push('  ' + rule.target + ': ' + rule.source(t, mode) + ';')
  lines.push('}', '')
  return lines.join('\n')
}

export function generateTokenMappingCSS(t: TokenPairs): string {
  const shared = buildSharedRules(t)
  const lightRules = [...buildSemanticRules(t, 'light'), ...buildComponentRules(t, 'light')]
  const darkRules = [...buildSemanticRules(t, 'dark'), ...buildComponentRules(t, 'dark')]

  // 角色展示配置（同 opencode：manifest char-config → CSS 变量，skin.css 以 var() 消费）
  const cfg = t['char-config'] ?? ({} as NonNullable<TokenPairs['char-config']>)
  const left = cfg['character-left'] ?? DEFAULT_CHAR_CONFIG['character-left']
  const right = cfg['character-right'] ?? DEFAULT_CHAR_CONFIG['character-right']
  const charRules: Rule[] = [
    { target: '--character-left-height', source: () => left.height },
    { target: '--character-right-height', source: () => right.height },
    { target: '--character-left-position', source: () => left.offset[0] + ' calc(100% - ' + left.offset[1] + ')' },
    { target: '--character-right-position', source: () => 'calc(100% - ' + right.offset[0] + ') calc(100% - ' + right.offset[1] + ')' },
  ]

  return [
    '/* Auto-generated by token-mapping.ts — do not edit manually */',
    '/* Maps universal skin-core tokens (light+dark pair) to Codex webview --color-token-* variables */',
    '/* Injected at runtime via CDP by pnpm apply:codex (main channel);',
       'config.toml chromeTheme (dist/token-mapping.toml) covers the native chrome layer */',
    '',
    '/* Shared color rules (independent of light/dark) */',
    formatBlock('html[data-skin]', shared, t, 'light'),
    '/* Character art config (from manifest char-config, consumed by skin.css) */',
    formatBlock('html[data-skin]', charRules, t, 'light'),
    '/* Light theme semantics */',
    formatBlock('html[data-skin].electron-light', lightRules, t, 'light'),
    '/* Dark theme semantics */',
    formatBlock('html[data-skin].electron-dark', darkRules, t, 'dark'),
  ].join('\n')
}

/** config.toml 原生 chrome 层：双套 chromeTheme 颜色键（accent/ink/surface + semanticColors） */
export function generateChromeThemeColors(t: TokenPairs): { light: ChromeThemeColors; dark: ChromeThemeColors } {
  const colors = (mode: ThemeMode): ChromeThemeColors => {
    const accent = brandLink(t, mode)
    const surface = S(t, mode, 'surface-base')
    return {
      accent,
      ink: S(t, mode, 'text-base'),
      surface,
      diffAdded: S(t, mode, 'success-base'),
      diffRemoved: S(t, mode, 'critical-base'),
      skill: contrastRatio(S(t, mode, 'accent'), surface) >= 3 ? S(t, mode, 'accent') : S(t, mode, 'info-base'),
    }
  }
  return { light: colors('light'), dark: colors('dark') }
}

export function generateChromeThemeToml(t: TokenPairs): string {
  const c = generateChromeThemeColors(t)
  const section = (mode: ThemeMode, colors: ChromeThemeColors) =>
    [
      '[desktop.appearance' + (mode === 'light' ? 'Light' : 'Dark') + 'ChromeTheme]',
      'accent = "' + colors.accent + '"',
      'ink = "' + colors.ink + '"',
      'surface = "' + colors.surface + '"',
      '',
      '[desktop.appearance' + (mode === 'light' ? 'Light' : 'Dark') + 'ChromeTheme.semanticColors]',
      'diffAdded = "' + colors.diffAdded + '"',
      'diffRemoved = "' + colors.diffRemoved + '"',
      'skill = "' + colors.skill + '"',
    ].join('\n')
  return [
    '# Auto-generated by token-mapping.ts — do not edit manually',
    '# Native chrome layer for Codex Desktop ([desktop] section of ~/.codex/config.toml)',
    '# NOTE: this channel styles the native window surface only; the in-app UI colors',
    '#       come from the CDP-injected token-mapping.css (pnpm apply:codex).',
    '',
    section('light', c.light),
    '',
    section('dark', c.dark),
    '',
  ].join('\n')
}

/** 官方 Settings → Appearance → Import 导入串（云持久化；导入时需自行选择内置 codeThemeId） */
export function generateThemeV1String(t: TokenPairs, mode: ThemeMode): string {
  const c = generateChromeThemeColors(t)[mode]
  const payload = {
    theme: {
      accent: c.accent,
      ink: c.ink,
      surface: c.surface,
      semanticColors: { diffAdded: c.diffAdded, diffRemoved: c.diffRemoved, skill: c.skill },
    },
    variant: mode,
  }
  return 'codex-theme-v1:' + JSON.stringify(payload)
}

// CLI: 读 tokens.json（light/dark 双套），写 dist 产物
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const tokensPath = path.resolve(__dirname, '../../../skin-core/dist/tokens.json')
  const distDir = path.resolve(__dirname, '../dist')

  if (!fs.existsSync(tokensPath)) {
    console.error('❌ tokens.json not found at ' + tokensPath + '. Run \"pnpm build-tokens\" first.')
    process.exit(1)
  }

  const tokens = JSON.parse(fs.readFileSync(tokensPath, 'utf8')) as TokenPairs
  fs.mkdirSync(distDir, { recursive: true })

  const css = generateTokenMappingCSS(tokens)
  fs.writeFileSync(path.join(distDir, 'token-mapping.css'), css)

  const toml = generateChromeThemeToml(tokens)
  fs.writeFileSync(path.join(distDir, 'token-mapping.toml'), toml)

  fs.writeFileSync(path.join(distDir, 'codex-theme-v1-light.txt'), generateThemeV1String(tokens, 'light'))
  fs.writeFileSync(path.join(distDir, 'codex-theme-v1-dark.txt'), generateThemeV1String(tokens, 'dark'))

  const count = (mode: ThemeMode) => buildSemanticRules(tokens, mode).length + buildComponentRules(tokens, mode).length
  console.log('✓ Generated dist/token-mapping.css (shared ' + buildSharedRules(tokens).length + ' + light ' + count('light') + ' + dark ' + count('dark') + ' mappings)')
  console.log('✓ Generated dist/token-mapping.toml (chromeTheme light+dark color keys)')
  console.log('✓ Generated dist/codex-theme-v1-light.txt / dark.txt (official import strings)')
}
