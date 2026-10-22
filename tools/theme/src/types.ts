// packages/skin-core/src/types.ts
// Core types for the universal skin pipeline.

/** An RGB color as [r, g, b] (0-255 each). */
export type RGB = [number, number, number]

/** A hex color string like "#526AA8". */
export type Hex = string

/**
 * The expanded token schema that skin-core guarantees.
 * Organized by category to cover common GUI theming needs.
 */
export const TOKEN_KEYS = [
  // ── Neutral scale (12 levels, light→dark) ──
  'neutral-50',
  'neutral-100',
  'neutral-200',
  'neutral-300',
  'neutral-400',
  'neutral-500',
  'neutral-600',
  'neutral-700',
  'neutral-800',
  'neutral-900',
  'neutral-1000',
  'neutral-1100',

  // ── Brand / primary scale (6 levels) ──
  'brand-100',
  'brand-300',
  'brand-500',
  'brand-600',
  'brand-700',
  'brand-900',

  // ── Semantic colors (4 groups × 3 levels) ──
  'success-weak',
  'success-base',
  'success-strong',
  'warning-weak',
  'warning-base',
  'warning-strong',
  'critical-weak',
  'critical-base',
  'critical-strong',
  'info-weak',
  'info-base',
  'info-strong',

  // ── Text ──
  'text-strong',
  'text-base',
  'text-weak',
  'text-weaker',
  'text-inverse',

  // ── Surface / background ──
  'surface-base',
  'surface-raised',
  'surface-strong',
  'surface-weak',

  // ── Border ──
  'border-base',
  'border-weak',
  'border-strong',
  'border-focus',

  // ── Input ──
  'input-base',
  'input-active',

  // ── Accent ──
  'accent',
] as const

export type TokenKey = (typeof TOKEN_KEYS)[number]

/** The output of buildTokens(): a mapping from token key to hex color. */
export type Tokens = Record<TokenKey, Hex>

/** Configuration for buildTokens(). */
export interface BuildOptions {
  /** Paths to source images (webp/png/jpg). */
  sources: string[]
  /** Number of color clusters to extract (default: 16). */
  k?: number
  /** Output path for tokens.json. */
  outPath?: string
  /**
   * 选中主题名（skin-assets 的 <name>.theme/）。主题只在 build-tokens 选择，
   * 写入 tokens.json 的 theme 字段，preview / apply 等其他模块跟随读取。
   */
  theme?: string
  /**
   * 角色展示配置（键为 character-left / character-right，字段可选，缺省用默认值：
   * offset ["0%", "0%"]、height "86%"；均为 CSS 值字符串）。
   * 写入 tokens.json 的 char-config 字段，
   * 供 apply 时生成 --character-*-height / --character-*-position CSS 变量。
   */
  charConfig?: Partial<
    Record<'character-left' | 'character-right', { offset?: [string, string]; height?: string }>
  >
  /** Optional: seed colors to bias clustering toward (e.g. status-ok green). */
  seedColors?: Hex[]
}

/** A color cluster result from k-means. */
export interface ColorCluster {
  color: RGB
  count: number
  /** L* brightness in [0, 100]. */
  brightness: number
}
