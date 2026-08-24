// packages/skin-core/src/generate-tokens.ts
// Map color clusters to the expanded token schema.
import type { ColorCluster, Tokens, RGB, Hex } from './types.js'

/**
 * Convert RGB to hex string.
 */
export function rgbToHex(rgb: RGB): Hex {
  return '#' + rgb.map((c) => c.toString(16).padStart(2, '0')).join('')
}

/**
 * Convert hex string to RGB.
 */
export function hexToRgb(hex: Hex): RGB {
  const match = hex.match(/^#?([0-9a-f]{6})$/i)
  if (!match) throw new Error(`Invalid hex color: ${hex}`)
  const value = parseInt(match[1], 16)
  return [(value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff]
}

/**
 * Convert RGB to HSL.
 */
export function rgbToHsl(rgb: RGB): [number, number, number] {
  const [r, g, b] = rgb.map((c) => c / 255)
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2

  if (max === min) {
    return [0, 0, l]
  }

  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  let h = 0

  if (max === r) {
    h = (g - b) / d + (g < b ? 6 : 0)
  } else if (max === g) {
    h = (b - r) / d + 2
  } else {
    h = (r - g) / d + 4
  }

  return [h / 6, s, l]
}

/**
 * Convert HSL to RGB.
 */
export function hslToRgb(h: number, s: number, l: number): RGB {
  let r: number, g: number, b: number

  if (s === 0) {
    r = g = b = l
  } else {
    const hue2rgb = (p: number, q: number, t: number) => {
      if (t < 0) t += 1
      if (t > 1) t -= 1
      if (t < 1 / 6) return p + (q - p) * 6 * t
      if (t < 1 / 2) return q
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6
      return p
    }

    const q = l < 0.5 ? l * (1 + s) : l + s - l * s
    const p = 2 * l - q
    r = hue2rgb(p, q, h + 1 / 3)
    g = hue2rgb(p, q, h)
    b = hue2rgb(p, q, h - 1 / 3)
  }

  return [Math.round(r * 255), Math.round(g * 255), Math.round(b * 255)]
}

/**
 * Adjust lightness of a color.
 */
export function adjustLightness(rgb: RGB, targetL: number): RGB {
  const [h, s] = rgbToHsl(rgb)
  return hslToRgb(h, s, targetL)
}

/**
 * Interpolate between two RGB colors.
 */
export function lerpRgb(a: RGB, b: RGB, t: number): RGB {
  return [
    Math.round(a[0] + (b[0] - a[0]) * t),
    Math.round(a[1] + (b[1] - a[1]) * t),
    Math.round(a[2] + (b[2] - a[2]) * t),
  ]
}

/**
 * Generate a scale of N colors from light to dark.
 */
export function generateScale(base: RGB, count: number, lightRange: [number, number] = [0.95, 0.2]): RGB[] {
  const [h, s] = rgbToHsl(base)
  const [lMax, lMin] = lightRange
  const scale: RGB[] = []

  for (let i = 0; i < count; i++) {
    const t = i / (count - 1)
    const l = lMax - (lMax - lMin) * t
    scale.push(hslToRgb(h, s, l))
  }

  return scale
}

/**
 * Classify clusters by saturation and brightness.
 * Neutrals: saturation < 0.25 (slightly relaxed so image palettes with
 * muted colors still yield enough grey anchors for the neutral scale).
 */
function classifyClusters(clusters: ColorCluster[]) {
  const neutral: ColorCluster[] = []
  const chromatic: ColorCluster[] = []

  for (const c of clusters) {
    const [, s] = rgbToHsl(c.color)
    if (s < 0.25) {
      neutral.push(c)
    } else {
      chromatic.push(c)
    }
  }

  // Sort by brightness
  neutral.sort((a, b) => a.brightness - b.brightness)
  chromatic.sort((a, b) => a.brightness - b.brightness)

  // Fallback: if the image has almost no grey, borrow the least-saturated
  // chromatic clusters so the neutral scale still spans dark→light.
  while (neutral.length < 2 && chromatic.length > 0) {
    const leastSaturated = chromatic.reduce((best, c) => {
      const [, bs] = rgbToHsl(best.color)
      const [, cs] = rgbToHsl(c.color)
      return cs < bs ? c : best
    })
    neutral.push(leastSaturated)
    chromatic.splice(chromatic.indexOf(leastSaturated), 1)
  }

  neutral.sort((a, b) => a.brightness - b.brightness)
  chromatic.sort((a, b) => a.brightness - b.brightness)

  return { neutral, chromatic }
}

/**
 * Guess semantic colors from chromatic clusters.
 * Requires reasonable saturation AND lightness — a near-black or desaturated
 * cluster is NOT a usable semantic color, even if its hue matches.
 * - Green-ish → success
 * - Yellow/orange-ish → warning
 * - Red-ish → critical
 * - Blue-ish → info
 */
function guessSemanticColors(chromatic: ColorCluster[]) {
  const semantic = {
    success: null as RGB | null,
    warning: null as RGB | null,
    critical: null as RGB | null,
    info: null as RGB | null,
  }

  const usable = (c: ColorCluster): boolean => {
    const [, s, l] = rgbToHsl(c.color)
    return s >= 0.25 && l >= 0.25 && l <= 0.75
  }

  for (const c of chromatic) {
    const [h] = rgbToHsl(c.color)
    const hue = h * 360

    if (!usable(c)) continue

    if (hue >= 80 && hue < 160 && !semantic.success) {
      semantic.success = c.color
    } else if (hue >= 30 && hue < 80 && !semantic.warning) {
      semantic.warning = c.color
    } else if ((hue >= 0 && hue < 30) || hue >= 340) {
      if (!semantic.critical) semantic.critical = c.color
    } else if (hue >= 200 && hue < 280 && !semantic.info) {
      semantic.info = c.color
    }
  }

  return semantic
}

/**
 * Map color clusters to the expanded token schema.
 *
 * Generates TWO complete palettes — light and dark — from the same
 * clusters. Consumers (token-mapping) pick one set per theme instead of
 * deriving a dark variant themselves.
 *
 * Strategy:
 * - Classify clusters into neutral (grey) and chromatic
 * - Neutral → generate 12-level scale
 * - Chromatic → pick brand, accent, semantic colors
 * - Light mode: surfaces on light end, text on dark end, semantic deep
 * - Dark mode:  surfaces on dark end, text on light end, semantic brightened
 *
 * @param clusters - Array of color clusters, sorted by brightness
 * @param mode - 'light' | 'dark'
 * @returns Tokens object with all required token keys filled
 */
export function generateTokens(clusters: ColorCluster[], mode: 'light' | 'dark' = 'light'): Tokens {
  if (clusters.length < 8) {
    throw new Error(`Need at least 8 color clusters, got ${clusters.length}`)
  }

  const { neutral, chromatic } = classifyClusters(clusters)

  if (neutral.length < 2) {
    throw new Error(`Need at least 2 neutral clusters, got ${neutral.length}`)
  }
  if (chromatic.length < 2) {
    throw new Error(`Need at least 2 chromatic clusters, got ${chromatic.length}`)
  }

  // ── Neutral scale (12 levels) ──
  // Fixed full lightness range [0.97 → 0.06]; only hue/saturation come from
  // the image. If we used the image's actual L range, the scale would be
  // squeezed mid-range (no true white/black) and text/bg contrast would fail.
  // Saturation is CLAMPED to a low value: character art often carries strong
  // skin/hair hues which would tint every neutral surface pink/beige and
  // clash with the app's cool-toned chrome.
  const lightest = neutral[neutral.length - 1].color
  const darkest = neutral[0].color
  const neutralHue = rgbToHsl(lightest)[0]
  const neutralSaturation = Math.min(rgbToHsl(lightest)[1], 0.08)
  const isDark = mode === 'dark'
  const neutralScale = Array.from({ length: 12 }, (_, i) => {
    const t = i / 11
    // Mode-aware ranges: light mode spans near-white → medium-dark for
    // light surfaces & dark text; dark mode spans near-white → near-black
    // with a deeper floor so dark surfaces read as truly dark.
    const l = isDark
      ? 0.85 - 0.81 * t  // 0.85 → 0.04
      : 0.97 - 0.82 * t  // 0.97 → 0.15
    return hslToRgb(neutralHue, neutralSaturation, l)
  })

  // ── Brand scale (6 levels) ──
  // Pick the most saturated chromatic color as brand base
  const brandBase = chromatic.reduce((best, c) => {
    const [, s] = rgbToHsl(c.color)
    const [, bestS] = rgbToHsl(best.color)
    return s > bestS ? c : best
  }, chromatic[0]).color
  // Light: deeper brand so links/emphasis reach ≥4.5:1 on light surfaces.
  // Dark:  brighter brand so links/emphasis reach ≥4.5:1 on dark surfaces.
  const brandScale = generateScale(brandBase, 6, isDark ? [0.95, 0.32] : [0.88, 0.15])

  // ── Semantic colors ──
  const semantic = guessSemanticColors(chromatic)

  // Translate semantic base: preserve hue+saturation, adjust lightness for
  // each mode so the base reads vivid against the mode's surface.
  // Fallback: if no cluster matched the semantic role, synthesize one.
  const translateBase = (raw: RGB | null, fallbackHue: number, fallbackSat: number) => {
    const base = raw || hslToRgb(fallbackHue, fallbackSat, isDark ? 0.55 : 0.35)
    const [h, s] = rgbToHsl(base)
    return hslToRgb(h, s, isDark ? 0.55 : 0.4)
  }
  const successBase = translateBase(semantic.success, 140 / 360, 0.65)
  const warningBase = translateBase(semantic.warning, 34 / 360, 1.0)
  const criticalBase = translateBase(semantic.critical, 0 / 360, 0.72)
  const infoBase = translateBase(semantic.info, rgbToHsl(brandBase)[0], Math.max(rgbToHsl(brandBase)[1], 0.5))

  // ── Accent ──
  // Pick a secondary chromatic color different from brand, then translate
  // its lightness for the mode so it reads distinct against the surface.
  const accentRaw =
    chromatic.find((c) => {
      const dist = Math.abs(rgbToHsl(c.color)[0] - rgbToHsl(brandBase)[0])
      return dist > 0.1
    })?.color || chromatic[Math.floor(chromatic.length / 2)].color
  const [accentH, accentS] = rgbToHsl(accentRaw)
  const accentBase = hslToRgb(accentH, accentS, isDark ? 0.7 : 0.85)

  // ── Build tokens ──
  const tokens: Tokens = {} as Tokens

  // Neutral scale
  tokens['neutral-50'] = rgbToHex(neutralScale[0])
  tokens['neutral-100'] = rgbToHex(neutralScale[1])
  tokens['neutral-200'] = rgbToHex(neutralScale[2])
  tokens['neutral-300'] = rgbToHex(neutralScale[3])
  tokens['neutral-400'] = rgbToHex(neutralScale[4])
  tokens['neutral-500'] = rgbToHex(neutralScale[5])
  tokens['neutral-600'] = rgbToHex(neutralScale[6])
  tokens['neutral-700'] = rgbToHex(neutralScale[7])
  tokens['neutral-800'] = rgbToHex(neutralScale[8])
  tokens['neutral-900'] = rgbToHex(neutralScale[9])
  tokens['neutral-1000'] = rgbToHex(neutralScale[10])
  tokens['neutral-1100'] = rgbToHex(neutralScale[11])

  // Brand scale
  tokens['brand-100'] = rgbToHex(brandScale[0])
  tokens['brand-300'] = rgbToHex(brandScale[1])
  tokens['brand-500'] = rgbToHex(brandScale[2])
  tokens['brand-600'] = rgbToHex(brandScale[3])
  tokens['brand-700'] = rgbToHex(brandScale[4])
  tokens['brand-900'] = rgbToHex(brandScale[5])

  // Semantic colors — weak/base/strong.
  // Light: weak = light tint, base = vivid, strong = deep.
  // Dark:  weak = deep tint, base = vivid, strong = light tint.
  if (isDark) {
    tokens['success-weak'] = rgbToHex(adjustLightness(successBase, 0.25))
    tokens['success-base'] = rgbToHex(successBase)
    tokens['success-strong'] = rgbToHex(adjustLightness(successBase, 0.8))
    tokens['warning-weak'] = rgbToHex(adjustLightness(warningBase, 0.28))
    tokens['warning-base'] = rgbToHex(warningBase)
    tokens['warning-strong'] = rgbToHex(adjustLightness(warningBase, 0.8))
    tokens['critical-weak'] = rgbToHex(adjustLightness(criticalBase, 0.3))
    tokens['critical-base'] = rgbToHex(criticalBase)
    tokens['critical-strong'] = rgbToHex(adjustLightness(criticalBase, 0.82))
    tokens['info-weak'] = rgbToHex(adjustLightness(infoBase, 0.28))
    tokens['info-base'] = rgbToHex(infoBase)
    tokens['info-strong'] = rgbToHex(adjustLightness(infoBase, 0.8))
  } else {
    tokens['success-weak'] = rgbToHex(adjustLightness(successBase, 0.85))
    tokens['success-base'] = rgbToHex(successBase)
    tokens['success-strong'] = rgbToHex(adjustLightness(successBase, 0.3))
    tokens['warning-weak'] = rgbToHex(adjustLightness(warningBase, 0.85))
    tokens['warning-base'] = rgbToHex(warningBase)
    tokens['warning-strong'] = rgbToHex(adjustLightness(warningBase, 0.35))
    tokens['critical-weak'] = rgbToHex(adjustLightness(criticalBase, 0.85))
    tokens['critical-base'] = rgbToHex(criticalBase)
    tokens['critical-strong'] = rgbToHex(adjustLightness(criticalBase, 0.3))
    tokens['info-weak'] = rgbToHex(adjustLightness(infoBase, 0.85))
    tokens['info-base'] = rgbToHex(infoBase)
    tokens['info-strong'] = rgbToHex(adjustLightness(infoBase, 0.3))
  }

  // ── Surface / border / input scale (brand-tinted) ──
  // Instead of pure neutral grey, these tokens carry the brand hue at a
  // meaningful fraction of the brand saturation so the UI feels thematic.
  // Text stays neutral for readability; surfaces/borders carry the tint.
  const [brandHue, brandSat] = rgbToHsl(brandBase)
  const surfaceSat = brandSat * (isDark ? 0.18 : 0.15)
  const borderSat = brandSat * (isDark ? 0.25 : 0.22)
  const inputSat = brandSat * (isDark ? 0.15 : 0.12)

  const surfaceScale = Array.from({ length: 12 }, (_, i) => {
    const t = i / 11
    const l = isDark
      ? 0.85 - 0.81 * t  // 0.85 → 0.04
      : 0.97 - 0.82 * t  // 0.97 → 0.15
    return hslToRgb(brandHue, surfaceSat, l)
  })
  const borderScale = Array.from({ length: 12 }, (_, i) => {
    const t = i / 11
    const l = isDark
      ? 0.85 - 0.81 * t
      : 0.97 - 0.82 * t
    return hslToRgb(brandHue, borderSat, l)
  })
  const inputScale = Array.from({ length: 12 }, (_, i) => {
    const t = i / 11
    const l = isDark
      ? 0.85 - 0.81 * t
      : 0.97 - 0.82 * t
    return hslToRgb(brandHue, inputSat, l)
  })

  // Text — light: dark text on light surface; dark: light text on dark surface.
  // Keep text on the neutral scale (readability over thematic tint).
  if (isDark) {
    tokens['text-strong'] = rgbToHex(neutralScale[0])
    tokens['text-base'] = rgbToHex(neutralScale[1])
    tokens['text-weak'] = rgbToHex(neutralScale[4])
    tokens['text-weaker'] = rgbToHex(neutralScale[5])
    tokens['text-inverse'] = rgbToHex(neutralScale[11])
  } else {
    tokens['text-strong'] = rgbToHex(neutralScale[11])
    tokens['text-base'] = rgbToHex(neutralScale[10])
    tokens['text-weak'] = rgbToHex(neutralScale[7])
    tokens['text-weaker'] = rgbToHex(neutralScale[6]) // ≥3:1 on surface (disabled text still needs to be legible)
    tokens['text-inverse'] = rgbToHex(neutralScale[0])
  }

  // Surface — brand-tinted so surfaces carry the theme's warmth.
  if (isDark) {
    tokens['surface-base'] = rgbToHex(surfaceScale[10])
    tokens['surface-raised'] = rgbToHex(surfaceScale[9])
    tokens['surface-strong'] = rgbToHex(surfaceScale[0])
    tokens['surface-weak'] = rgbToHex(surfaceScale[8])
  } else {
    tokens['surface-base'] = rgbToHex(surfaceScale[1])
    tokens['surface-raised'] = rgbToHex(surfaceScale[0])
    tokens['surface-strong'] = rgbToHex(surfaceScale[11])
    tokens['surface-weak'] = rgbToHex(surfaceScale[2])
  }

  // Border — brand-tinted for thematic UI chrome.
  if (isDark) {
    tokens['border-base'] = rgbToHex(borderScale[5])
    tokens['border-weak'] = rgbToHex(borderScale[6])
    tokens['border-strong'] = rgbToHex(borderScale[4])
    tokens['border-focus'] = rgbToHex(brandScale[2])
  } else {
    tokens['border-base'] = rgbToHex(borderScale[7])
    tokens['border-weak'] = rgbToHex(borderScale[6])
    tokens['border-strong'] = rgbToHex(borderScale[8])
    tokens['border-focus'] = rgbToHex(brandScale[3])
  }

  // Input — brand-tinted, slightly less than surface.
  if (isDark) {
    tokens['input-base'] = rgbToHex(inputScale[9])
    tokens['input-active'] = rgbToHex(inputScale[10])
  } else {
    tokens['input-base'] = rgbToHex(inputScale[0])
    tokens['input-active'] = rgbToHex(inputScale[1])
  }

  // Accent
  tokens['accent'] = rgbToHex(accentBase)

  return tokens
}

/**
 * Generate both light and dark token sets from the same clusters.
 */
export function generateTokenPairs(clusters: ColorCluster[]): { light: Tokens; dark: Tokens } {
  return {
    light: generateTokens(clusters, 'light'),
    dark: generateTokens(clusters, 'dark'),
  }
}

/**
 * Validate that all required tokens are present.
 */
export function validateTokens(tokens: Partial<Tokens>): tokens is Tokens {
  const required = [
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
    'brand-100',
    'brand-300',
    'brand-500',
    'brand-600',
    'brand-700',
    'brand-900',
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
    'text-strong',
    'text-base',
    'text-weak',
    'text-weaker',
    'text-inverse',
    'surface-base',
    'surface-raised',
    'surface-strong',
    'surface-weak',
    'border-base',
    'border-weak',
    'border-strong',
    'border-focus',
    'input-base',
    'input-active',
    'accent',
  ]
  return required.every((key) => tokens[key as keyof Tokens] !== undefined)
}
