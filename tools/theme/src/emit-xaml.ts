// tools/theme/src/emit-xaml.ts
// 发射器：tokens.json（46 token × light/dark）→ WinUI 3 Theme.xaml + ThemeInfo.json + PNG/JPG 资产。
//
// 输出目录：themes/<id>.theme/generated/
//   Theme.xaml        ResourceDictionary.ThemeDictionaries（Default=暗 / Light / HighContrast）
//   ThemeInfo.json    主题元数据 + 少量代码侧需要的色值（任务栏徽标、托盘图标）
//   Assets/*          角色立绘 PNG（需 alpha）/ 氛围背景 JPG（无 alpha）
//
// app 侧契约：把 generated/ 以 ms-appx:///Themes/<id>/ 的路径打包进应用。
import fs from 'node:fs'
import path from 'node:path'
import sharp from 'sharp'
import { TOKEN_KEYS } from './types.js'
import { getThemeDir, getThemeMeta, getThemePackageDir, getAllRoleImagePaths, type ThemeMeta } from './assets-loader.js'

/** 明暗两套 token */
export interface TokenPair {
  light: Record<string, string>
  dark: Record<string, string>
}

type Role =
  | 'surface'
  | 'raised'
  | 'text'
  | 'textMuted'
  | 'textInverse'
  | 'border'
  | 'accent'
  | 'success'
  | 'warning'
  | 'critical'
  | 'info'
  | 'neutral'

/**
 * Fluent 2 / WinUI 3 的控件颜色资源映射。
 * 每一条会同时发射 `<Color x:Key="X">` 与 `<SolidColorBrush x:Key="XBrush">`——
 * WinUI 的控件样式两种都引用，只覆盖其中一种是常见的坑。
 * 键名取自 WinUI 3 generic.xaml 的颜色族；M0 阶段需在真实应用里逐键视觉校验。
 */
const FLUENT_KEYS: ReadonlyArray<{ key: string; token: string; role: Role }> = [
  // 文字
  { key: 'TextFillColorPrimary', token: 'text-strong', role: 'text' },
  { key: 'TextFillColorSecondary', token: 'text-base', role: 'textMuted' },
  { key: 'TextFillColorTertiary', token: 'text-weak', role: 'textMuted' },
  { key: 'TextFillColorDisabled', token: 'text-weaker', role: 'textMuted' },
  { key: 'TextFillColorInverse', token: 'text-inverse', role: 'textInverse' },
  { key: 'TextOnAccentFillColorPrimary', token: 'text-inverse', role: 'textInverse' },
  { key: 'TextOnAccentFillColorSecondary', token: 'text-inverse', role: 'textInverse' },
  { key: 'TextOnAccentFillColorSelectedText', token: 'text-inverse', role: 'textInverse' },

  // 背景 / 表面
  { key: 'SolidBackgroundFillColorBase', token: 'surface-base', role: 'surface' },
  { key: 'SolidBackgroundFillColorBaseAlt', token: 'surface-raised', role: 'raised' },
  { key: 'SolidBackgroundFillColorSecondary', token: 'surface-raised', role: 'raised' },
  { key: 'SolidBackgroundFillColorTertiary', token: 'surface-weak', role: 'raised' },
  { key: 'SolidBackgroundFillColorQuarternary', token: 'surface-weak', role: 'raised' },
  { key: 'LayerFillColorDefault', token: 'surface-raised', role: 'raised' },
  { key: 'LayerFillColorAlt', token: 'surface-weak', role: 'raised' },
  { key: 'CardBackgroundFillColorDefault', token: 'surface-raised', role: 'raised' },
  { key: 'CardBackgroundFillColorSecondary', token: 'surface-weak', role: 'raised' },
  { key: 'ControlSolidFillColorDefault', token: 'surface-raised', role: 'raised' },
  { key: 'ControlFillColorDefault', token: 'input-base', role: 'raised' },
  { key: 'ControlFillColorSecondary', token: 'surface-weak', role: 'raised' },
  { key: 'ControlFillColorTertiary', token: 'surface-weak', role: 'raised' },
  { key: 'ControlFillColorInputActive', token: 'input-active', role: 'raised' },
  { key: 'ControlFillColorDisabled', token: 'surface-weak', role: 'surface' },
  { key: 'ControlAltFillColorSecondary', token: 'surface-weak', role: 'raised' },
  { key: 'ControlAltFillColorTertiary', token: 'surface-weak', role: 'raised' },
  { key: 'ControlAltFillColorQuarternary', token: 'surface-weak', role: 'raised' },
  { key: 'ControlStrongFillColorDefault', token: 'text-weak', role: 'neutral' },
  { key: 'ControlStrongFillColorDisabled', token: 'text-weaker', role: 'neutral' },
  { key: 'SubtleFillColorSecondary', token: 'surface-weak', role: 'raised' },
  { key: 'SubtleFillColorTertiary', token: 'surface-weak', role: 'raised' },

  // 描边 / 焦点
  { key: 'ControlStrokeColorDefault', token: 'border-base', role: 'border' },
  { key: 'ControlStrokeColorSecondary', token: 'border-weak', role: 'border' },
  { key: 'ControlStrokeColorOnAccentDefault', token: 'text-inverse', role: 'textInverse' },
  { key: 'ControlStrokeColorOnAccentSecondary', token: 'text-inverse', role: 'textInverse' },
  { key: 'ControlStrongStrokeColorDefault', token: 'border-strong', role: 'border' },
  { key: 'CardStrokeColorDefault', token: 'border-base', role: 'border' },
  { key: 'DividerStrokeColorDefault', token: 'border-base', role: 'border' },
  { key: 'FocusStrokeColorOuter', token: 'border-focus', role: 'accent' },
  { key: 'FocusStrokeColorInner', token: 'surface-base', role: 'surface' },

  // 强调色（Fluent 的 AccentFill / SystemAccent 双族）
  { key: 'AccentFillColorDefault', token: 'brand-500', role: 'accent' },
  { key: 'AccentFillColorSecondary', token: 'brand-600', role: 'accent' },
  { key: 'AccentFillColorTertiary', token: 'brand-700', role: 'accent' },
  { key: 'AccentFillColorDisabled', token: 'border-weak', role: 'neutral' },
  { key: 'SystemAccentColor', token: 'brand-500', role: 'accent' },
  { key: 'SystemAccentColorLight1', token: 'brand-300', role: 'accent' },
  { key: 'SystemAccentColorLight2', token: 'brand-100', role: 'accent' },
  { key: 'SystemAccentColorDark1', token: 'brand-600', role: 'accent' },
  { key: 'SystemAccentColorDark2', token: 'brand-700', role: 'accent' },
  { key: 'SystemAccentColorDark3', token: 'brand-900', role: 'accent' },

  // 语义色
  { key: 'SystemFillColorSuccess', token: 'success-base', role: 'success' },
  { key: 'SystemFillColorSuccessBackground', token: 'success-weak', role: 'success' },
  { key: 'SystemFillColorCaution', token: 'warning-base', role: 'warning' },
  { key: 'SystemFillColorCautionBackground', token: 'warning-weak', role: 'warning' },
  { key: 'SystemFillColorCritical', token: 'critical-base', role: 'critical' },
  { key: 'SystemFillColorCriticalBackground', token: 'critical-weak', role: 'critical' },
  { key: 'SystemFillColorAttention', token: 'info-base', role: 'info' },
  { key: 'SystemFillColorAttentionBackground', token: 'info-weak', role: 'info' },
  { key: 'SystemFillColorNeutral', token: 'text-weaker', role: 'neutral' },
  { key: 'SystemFillColorSolidNeutral', token: 'text-weak', role: 'neutral' },
]

/**
 * 只有 Brush 形态、没有同名 Color 的资源（类型不同，不能套用上面的双发射规则）。
 * 数量刻意保持在最小集，避免猜错类型导致启动崩溃。
 */
const BRUSH_ONLY_KEYS: ReadonlyArray<{ key: string; token: string; role: Role }> = [
  { key: 'ApplicationPageBackgroundThemeBrush', token: 'surface-base', role: 'surface' },
  { key: 'ListViewItemBackgroundSelected', token: 'brand-100', role: 'accent' },
  { key: 'ListViewItemBackgroundSelectedPointerOver', token: 'brand-300', role: 'accent' },
  { key: 'NavigationViewItemBackgroundSelected', token: 'surface-weak', role: 'raised' },
]

/** 通透语义：这些资源固定为完全透明，不取 token */
const TRANSPARENT_KEYS = [
  'ControlFillColorTransparent',
  'SubtleFillColorTransparent',
  'ControlStrokeColorTransparent',
] as const

const HC_COLOR: Record<Role, string> = {
  surface: 'SystemColorWindowColor',
  raised: 'SystemColorWindowColor',
  text: 'SystemColorWindowTextColor',
  textMuted: 'SystemColorWindowTextColor',
  textInverse: 'SystemColorHighlightTextColor',
  border: 'SystemColorWindowTextColor',
  accent: 'SystemColorHighlightColor',
  success: 'SystemColorWindowTextColor',
  warning: 'SystemColorWindowTextColor',
  critical: 'SystemColorWindowTextColor',
  info: 'SystemColorWindowTextColor',
  neutral: 'SystemColorWindowTextColor',
}

function normalizeHex(hex: string): string {
  const h = hex.trim().replace(/^#/, '').toUpperCase()
  if (!/^[0-9A-F]{6}$/.test(h)) {
    throw new Error(`token color must be a 6-digit hex string, got '${hex}'`)
  }
  return `#FF${h}`
}

function colorLine(key: string, hex: string, indent: string): string {
  return `${indent}<Color x:Key="${key}">${normalizeHex(hex)}</Color>`
}

function brushLine(key: string, hex: string, indent: string): string {
  return `${indent}<SolidColorBrush x:Key="${key}Brush" Color="${normalizeHex(hex)}" />`
}

function hcColorLine(key: string, role: Role, indent: string): string {
  return `${indent}<Color x:Key="${key}">{ThemeResource ${HC_COLOR[role]}}</Color>`
}

function hcBrushLine(key: string, role: Role, indent: string): string {
  return `${indent}<SolidColorBrush x:Key="${key}Brush" Color="{ThemeResource ${HC_COLOR[role]}}" />`
}

/** token key → PascalCase 前缀（neutral-50 → CirnoNeutral50） */
function tokenBrushKey(token: string): string {
  const pascal = token
    .split('-')
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join('')
  return `Cirno${pascal}`
}

/**
 * 把 46 个原始 token 全量导出为 Cirno*Brush。
 * Fluent 覆盖只解决标准控件；侧边栏、时间线卡片、徽标这些自绘部件直接用这套调色板。
 */
function renderCirnoBrushes(tokens: Record<string, string>, indent: string): string[] {
  return TOKEN_KEYS.map(
    (key) =>
      `${indent}<SolidColorBrush x:Key="${tokenBrushKey(key)}Brush" Color="${normalizeHex(tokens[key])}" />`
  )
}

/** WCAG 相对亮度对比度 */
function contrast(a: string, b: string): number {
  const lum = (hex: string) => {
    const v = normalizeHex(hex).slice(3)
    const ch = [0, 2, 4].map((i) => parseInt(v.slice(i, i + 2), 16) / 255)
    const lin = ch.map((c) => (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4)))
    return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2]
  }
  const [l1, l2] = [lum(a), lum(b)].sort((x, y) => y - x)
  return (l1 + 0.05) / (l2 + 0.05)
}

/**
 * 正文可读性闸门：37 项里最要紧的两组——
 * 强调文字压在基础面上、次级文字压在抬升面上。
 * 低于 3:1 直接判构建失败（几乎肯定无法阅读）；3–4.5 之间只警告（弱提示文字可接受）。
 */
export function validateTokens(pair: TokenPair): string[] {
  const pairs: Array<[string, 'light' | 'dark', string, string]> = [
    ['正文/基础面', 'light', 'text-strong', 'surface-base'],
    ['正文/基础面', 'dark', 'text-strong', 'surface-base'],
    ['次级文字/抬升面', 'light', 'text-base', 'surface-raised'],
    ['次级文字/抬升面', 'dark', 'text-base', 'surface-raised'],
    ['弱提示文字/基础面', 'light', 'text-weak', 'surface-base'],
    ['弱提示文字/基础面', 'dark', 'text-weak', 'surface-base'],
  ]
  const warnings: string[] = []
  for (const [label, scheme, fg, bg] of pairs) {
    const ratio = contrast(pair[scheme][fg], pair[scheme][bg])
    const text = `${scheme} ${label}（${fg} on ${bg}）= ${ratio.toFixed(2)}:1`
    if (ratio < 3) throw new Error(`对比度过低：${text}`)
    if (ratio < 4.5) warnings.push(text)
  }
  return warnings
}

/** 生成一个 ThemeDictionaries 分支的内容（纯函数，便于快照测试） */
export function renderThemeDictionary(tokens: Record<string, string>, scheme: 'light' | 'dark' | 'hc'): string {
  const out: string[] = []
  const ind = '        '
  if (scheme === 'hc') {
    for (const { key, role } of FLUENT_KEYS) {
      out.push(hcColorLine(key, role, ind), hcBrushLine(key, role, ind))
    }
    for (const { key, role } of BRUSH_ONLY_KEYS) {
      out.push(hcBrushLine(key.replace(/Brush$/, ''), role, ind))
    }
  } else {
    out.push(...renderCirnoBrushes(tokens, ind))
    for (const { key, token } of FLUENT_KEYS) {
      const hex = tokens[token]
      if (!hex) throw new Error(`token '${token}' missing from ${scheme} set (needed by ${key})`)
      out.push(colorLine(key, hex, ind), brushLine(key, hex, ind))
    }
    for (const { key, token } of BRUSH_ONLY_KEYS) {
      const hex = tokens[token]
      if (!hex) throw new Error(`token '${token}' missing from ${scheme} set (needed by ${key})`)
      out.push(`${ind}<SolidColorBrush x:Key="${key}" Color="${normalizeHex(hex)}" />`)
    }
  }
  for (const key of TRANSPARENT_KEYS) {
    out.push(`${ind}<Color x:Key="${key}">Transparent</Color>`)
    out.push(`${ind}<SolidColorBrush x:Key="${key}Brush" Color="Transparent" />`)
  }
  return out.join('\n')
}

/** 生成完整 Theme.xaml（纯函数） */
export function buildThemeXaml(
  pair: TokenPair,
  meta: ThemeMeta,
  artUris: Record<string, string>
): string {
  const { id, displayName, description, art, charConfig } = meta
  const xmlEscape = (s: string) =>
    s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

  const artBlocks: string[] = []
  if (artUris['background-day']) {
    artBlocks.push(
      `    <BitmapImage x:Key="CirnoArtBackgroundDay" UriSource="${artUris['background-day']}" />`,
      `    <BitmapImage x:Key="CirnoArtBackgroundNight" UriSource="${artUris['background-night']}" />`
    )
  }
  if (artUris['character-left']) {
    artBlocks.push(
      `    <BitmapImage x:Key="CirnoArtCharacterLeft" UriSource="${artUris['character-left']}" />`,
      `    <BitmapImage x:Key="CirnoArtCharacterRight" UriSource="${artUris['character-right']}" />`
    )
  }

  return `<?xml version="1.0" encoding="utf-8"?>
<!--
  Cirno 主题：${displayName}（${id}）
  ${description ?? ''}
  由 tools/theme 于构建期生成，请勿手改。
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <ResourceDictionary.ThemeDictionaries>

    <!-- WinUI 的「深色」沿用 Default 键，不是 "Dark" -->
    <ResourceDictionary x:Key="Default">
${renderThemeDictionary(pair.dark, 'dark')}
    </ResourceDictionary>

    <ResourceDictionary x:Key="Light">
${renderThemeDictionary(pair.light, 'light')}
    </ResourceDictionary>

    <ResourceDictionary x:Key="HighContrast">
${renderThemeDictionary(pair.dark, 'hc')}
    </ResourceDictionary>

  </ResourceDictionary.ThemeDictionaries>

  <!-- 主题元数据（供代码读取） -->
  <x:String x:Key="CirnoThemeId">${xmlEscape(id)}</x:String>
  <x:String x:Key="CirnoThemeDisplayName">${xmlEscape(displayName)}</x:String>
  <x:Double x:Key="CirnoArtOverlayOpacity">${art.overlayOpacity}</x:Double>
  <x:Double x:Key="CirnoArtCharacterOpacity">${art.characterOpacity}</x:Double>
  <x:Double x:Key="CirnoArtScrimOpacity">${art.scrimOpacity}</x:Double>
  <x:String x:Key="CirnoCharacterLeftHeight">${xmlEscape(charConfig['character-left'].height)}</x:String>
  <x:String x:Key="CirnoCharacterRightHeight">${xmlEscape(charConfig['character-right'].height)}</x:String>

${artBlocks.join('\n')}
</ResourceDictionary>
`
}

/** 角色 → 输出资产文件名与格式（背景走 JPG，立绘保留 alpha 走 PNG） */
const ASSET_PLAN: Record<string, { file: string; format: 'jpg' | 'png'; maxWidth: number }> = {
  'background-day': { file: 'background-day.jpg', format: 'jpg', maxWidth: 2560 },
  'background-night': { file: 'background-night.jpg', format: 'jpg', maxWidth: 2560 },
  'character-left': { file: 'character-left.png', format: 'png', maxWidth: 1600 },
  'character-right': { file: 'character-right.png', format: 'png', maxWidth: 1600 },
}

/** 把主题的 webp 素材转成 WinUI 可渲染的位图（Image 不支持 webp） */
async function emitAssets(theme: string, outDir: string, themeId: string) {
  const roles = getAllRoleImagePaths(theme)
  const assetsDir = path.join(outDir, 'Assets')
  fs.mkdirSync(assetsDir, { recursive: true })

  const uris: Record<string, string> = {}
  const written: string[] = []

  for (const [role, srcPath] of Object.entries(roles)) {
    const plan = ASSET_PLAN[role]
    if (!plan) continue
    const dest = path.join(assetsDir, plan.file)
    let img = sharp(srcPath)
    const meta = await img.metadata()
    if (meta.width && meta.width > plan.maxWidth) {
      img = img.resize({ width: plan.maxWidth })
    }
    if (plan.format === 'jpg') {
      await img.flatten({ background: '#000000' }).jpeg({ quality: 88, mozjpeg: true }).toFile(dest)
    } else {
      await img.png({ compressionLevel: 9, palette: false }).toFile(dest)
    }
    uris[role] = `ms-appx:///Themes/${themeId}/Assets/${plan.file}`
    written.push(dest)
  }
  return { uris, written }
}

export interface EmitResult {
  theme: string
  outDir: string
  xamlPath: string
  infoPath: string
  assets: string[]
}

/**
 * 发射单个主题包：Theme.xaml + ThemeInfo.json + Assets/*
 */
export async function emitTheme(
  theme: string,
  pair: TokenPair,
  outDir?: string
): Promise<EmitResult> {
  const meta = getThemeMeta(theme)
  // 产物目录 artifacts/themes/<id>/ 与 ms-appx:///Themes/<id>/ 一一对应
  const dir = outDir ?? getThemePackageDir(theme)
  fs.mkdirSync(dir, { recursive: true })

  for (const w of validateTokens(pair)) {
    console.log(`  ⚠ 对比度偏低：${w}`)
  }

  const { uris, written } = await emitAssets(theme, dir, meta.id)

  const xamlPath = path.join(dir, 'Theme.xaml')
  fs.writeFileSync(xamlPath, buildThemeXaml(pair, meta, uris), 'utf8')

  // 代码侧需要的最小色板（任务栏徽标 / 托盘图标 / 无 XAML 上下文的绘制）
  const pick = (scheme: 'light' | 'dark', keys: string[]) =>
    Object.fromEntries(keys.map((k) => [k, pair[scheme][k]]))
  const codePaletteKeys = ['accent', 'surface-base', 'text-strong', 'success-base', 'warning-base', 'critical-base']

  const info = {
    schema: 'cirno-theme-package-v1',
    id: meta.id,
    displayName: meta.displayName,
    description: meta.description,
    xaml: `Themes/${meta.id}/Theme.xaml`,
    assets: uris,
    art: meta.art,
    charConfig: meta.charConfig,
    palette: {
      light: pick('light', codePaletteKeys),
      dark: pick('dark', codePaletteKeys),
    },
  }
  const infoPath = path.join(dir, 'ThemeInfo.json')
  fs.writeFileSync(infoPath, JSON.stringify(info, null, 2), 'utf8')

  return { theme: meta.id, outDir: dir, xamlPath, infoPath, assets: written }
}

/** 汇总所有已发射主题的注册表（app 的主题选择器读这个文件） */
export function writeThemeRegistry(infoPaths: string[], outPath: string) {
  const entries = infoPaths.map((p) => {
    const info = JSON.parse(fs.readFileSync(p, 'utf8'))
    return {
      id: info.id,
      displayName: info.displayName,
      description: info.description,
      xaml: info.xaml,
      info: `Themes/${info.id}/ThemeInfo.json`,
    }
  })
  fs.mkdirSync(path.dirname(outPath), { recursive: true })
  fs.writeFileSync(
    outPath,
    JSON.stringify({ schema: 'cirno-theme-registry-v1', builtin: 'default', themes: entries }, null, 2),
    'utf8'
  )
  return outPath
}
