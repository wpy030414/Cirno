// src/assets-loader.ts
// Helper to load standard role images from skin-assets via per-theme manifest.json.
// skin-assets 下每个 <name>.theme/ 目录是一个主题，拥有自己的 manifest.json
// （4 个标准角色映射 + colorSource 取色声明）。所有加载函数都必须显式指定主题，
// 未指定时由 selectTheme() 交互选择或报错——绝不静默选主题。
import fs from 'node:fs'
import path from 'node:path'
import readline from 'node:readline'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

export type StandardRole = 'background-day' | 'background-night' | 'character-left' | 'character-right'

const STANDARD_ROLES: StandardRole[] = [
  'background-day',
  'background-night',
  'character-left',
  'character-right',
]

const THEME_SUFFIX = '.theme'

/** 角色展示配置：offset = 距边/距底偏移，height = 立绘高度——均为 CSS 值字符串 */
export interface CharConfig {
  /** [x, y] CSS 值字符串（如 ["-30px", "-20%"]）：x = 距边缘（左角色距左、右角色距右），y = 距底 */
  offset?: [string, string]
  /** 立绘高度 CSS 值（如 "80vh"、"86%"） */
  height?: string
}

/** 完整解析后的角色配置（offset/height 必填） */
export type ResolvedCharConfig = {
  offset: [string, string]
  height: string
}

interface Manifest {
  schema: string
  /** 可选——新版 manifest 已删除该字段，保留仅为兼容旧文件 */
  description?: string
  roles: Record<StandardRole, string>
  /**
   * 取色来源，喂给取色管线的图。条目可以是**角色 key**（旧契约，
   * 如 "character-left"）或**主题目录内的文件名**（新用法，
   * 如 "chocola_pose.png"）——两种写法都支持，场景背景不应声明于此。
   */
  colorSource: string[]
  /**
   * 角色展示配置（可选）。键为角色 key（character-left / character-right），
   * 每个角色可配 offset（[x, y] CSS 值字符串，距边/距底）与 height（CSS 值），
   * 未列出的角色与未配字段用默认值。由 build-tokens 写入 tokens.json，
   * 供 apply 时生成 --character-*-height / --character-*-position CSS 变量。
   */
  'char-config'?: Partial<Record<StandardRole, CharConfig>>
}

/** 角色配置默认值：未配置时左右均贴边贴底（offset ["0%", "0%"]）、高度 86% */
export const DEFAULT_CHAR_CONFIG: Record<'character-left' | 'character-right', ResolvedCharConfig> = {
  'character-left': { offset: ['0%', '0%'], height: '86%' },
  'character-right': { offset: ['0%', '0%'], height: '86%' },
}

/**
 * 读取主题的角色展示配置（manifest 的 char-config），未列出的角色与
 * 未配字段补默认值。返回完整的两角色配置，供 build-tokens 写入 tokens.json。
 */
export function getCharConfig(theme: string): Record<'character-left' | 'character-right', ResolvedCharConfig> {
  const manifest = loadManifest(theme)
  const configured = manifest['char-config'] ?? {}
  const resolve = (role: 'character-left' | 'character-right'): ResolvedCharConfig => ({
    offset: configured[role]?.offset ?? DEFAULT_CHAR_CONFIG[role].offset,
    height: configured[role]?.height ?? DEFAULT_CHAR_CONFIG[role].height,
  })
  return {
    'character-left': resolve('character-left'),
    'character-right': resolve('character-right'),
  }
}

// Resolve skin-assets path from skin-core
function getSkinAssetsDir(): string {
  return path.resolve(__dirname, '../../skin-assets')
}

/** 归一化主题名：接受 `maid-atelier` 或 `maid-atelier.theme` 两种写法 */
function normalizeThemeName(name: string): string {
  return name.endsWith(THEME_SUFFIX) ? name.slice(0, -THEME_SUFFIX.length) : name
}

/** 列出 skin-assets 下所有可用主题（<name>.theme/ 目录且含 manifest.json），按名排序 */
export function listThemes(): string[] {
  const assetsDir = getSkinAssetsDir()
  if (!fs.existsSync(assetsDir)) return []
  return fs
    .readdirSync(assetsDir, { withFileTypes: true })
    .filter(
      (d) =>
        d.isDirectory() &&
        d.name.endsWith(THEME_SUFFIX) &&
        fs.existsSync(path.join(assetsDir, d.name, 'manifest.json'))
    )
    .map((d) => d.name.slice(0, -THEME_SUFFIX.length))
    .sort()
}

/** 主题名 → 物理目录（<name>.theme/），不存在时报错并列出可用主题 */
export function getThemeDir(name: string): string {
  const themeName = normalizeThemeName(name)
  const dir = path.join(getSkinAssetsDir(), themeName + THEME_SUFFIX)
  if (!fs.existsSync(dir)) {
    const available = listThemes().join(', ') || '(none)'
    throw new Error(`theme '${themeName}' not found under packages/skin-assets. Available themes: ${available}`)
  }
  return dir
}

/**
 * 解析用户选定的主题，优先级：--theme 参数 > SKIN_THEME 环境变量 > 交互式终端选择。
 * 交互选择只在 TTY 下进行；非 TTY（脚本/CI）时报错并列出可用主题。
 * 0 个主题直接报错。返回归一化后的主题名（无 .theme 后缀）。
 *
 * 注意：主题选择**只发生在 build-tokens**。preview / apply 等其他模块
 * 不调用本函数，一律通过 getActiveTheme() 跟随 build-tokens 选定的主题。
 */
export async function selectTheme(themeArg?: string): Promise<string> {
  const explicit = themeArg ?? process.env.SKIN_THEME
  if (explicit) {
    // 提前校验，错误信息带上可用主题列表
    getThemeDir(explicit)
    return normalizeThemeName(explicit)
  }

  const themes = listThemes()
  if (themes.length === 0) {
    throw new Error(`no themes found under ${getSkinAssetsDir()}/ — expected <name>${THEME_SUFFIX}/ dirs with manifest.json`)
  }
  if (process.stdin.isTTY) {
    return promptThemeSelection(themes)
  }
  throw new Error(
    `multiple themes available: ${themes.join(', ')} — pass --theme <name> or set SKIN_THEME=<name>`
  )
}

/** 交互式编号菜单：回车取第一个，支持数字或主题名 */
async function promptThemeSelection(themes: string[]): Promise<string> {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout })
  try {
    for (;;) {
      console.log(`Available themes in packages/skin-assets (${themes.length}):`)
      for (let i = 0; i < themes.length; i++) {
        console.log(`  ${i + 1}) ${themes[i]}`)
      }
      const answer = await new Promise<string>((resolve) => rl.question(`Select theme [${themes[0]}]: `, resolve))
      const trimmed = answer.trim()
      if (trimmed === '') return themes[0]
      const idx = Number(trimmed)
      if (Number.isInteger(idx) && idx >= 1 && idx <= themes.length) return themes[idx - 1]
      if (themes.includes(trimmed)) return trimmed
      console.error(`  Invalid selection: '${trimmed}' — enter a number or theme name`)
    }
  } finally {
    rl.close()
  }
}

// tokens.json 路径（skin-core/dist）——活动主题记录在顶层 theme 字段
function getTokensPath(): string {
  return path.resolve(__dirname, '../dist/tokens.json')
}

/**
 * 读取「活动主题」——由 build-tokens 选定并写入 dist/tokens.json 的 theme 字段。
 * preview / apply 等其他模块不选择主题，一律跟随本函数返回的主题。
 */
export function getActiveTheme(): string {
  const tokensPath = getTokensPath()
  if (!fs.existsSync(tokensPath)) {
    throw new Error(`no active theme: ${tokensPath} not found — run 'pnpm build-tokens' first (theme is selected there)`)
  }
  const tokens = JSON.parse(fs.readFileSync(tokensPath, 'utf8')) as { theme?: string }
  if (!tokens.theme) {
    throw new Error(`no active theme recorded in ${tokensPath} — re-run 'pnpm build-tokens' (theme is selected there)`)
  }
  // 校验主题仍存在（目录被删除时报错并列出可用主题）
  getThemeDir(tokens.theme)
  return normalizeThemeName(tokens.theme)
}

// Load manifest.json from a theme directory
function loadManifest(theme: string): Manifest {
  const themeDir = getThemeDir(theme)
  const manifestPath = path.join(themeDir, 'manifest.json')
  if (!fs.existsSync(manifestPath)) {
    throw new Error(`manifest.json not found: ${manifestPath}`)
  }
  const raw = JSON.parse(fs.readFileSync(manifestPath, 'utf8')) as Partial<Manifest>
  if (!raw.roles || typeof raw.roles !== 'object') {
    throw new Error(`manifest.json must declare a "roles" object: ${manifestPath}`)
  }
  for (const role of STANDARD_ROLES) {
    if (typeof raw.roles[role] !== 'string' || raw.roles[role].length === 0) {
      throw new Error(`role '${role}' not found in manifest.json`)
    }
  }
  if (!Array.isArray(raw.colorSource) || raw.colorSource.length === 0) {
    throw new Error('manifest.json must declare a non-empty "colorSource" array (roles feeding the color pipeline)')
  }
  if (raw['char-config'] !== undefined) {
    if (typeof raw['char-config'] !== 'object' || raw['char-config'] === null || Array.isArray(raw['char-config'])) {
      throw new Error('manifest.json "char-config" must be an object like { "character-right": { "offset": [10, 10], "height": 78 } }')
    }
    for (const [role, config] of Object.entries(raw['char-config'])) {
      if (typeof config !== 'object' || config === null) {
        throw new Error(`manifest.json "char-config" entry '${role}' must be an object with optional "offset" and "height"`)
      }
      if (config.offset !== undefined) {
        const ok =
          Array.isArray(config.offset) &&
          config.offset.length === 2 &&
          config.offset.every((v) => typeof v === 'string' && v.trim().length > 0)
        if (!ok) {
          throw new Error(`manifest.json "char-config" entry '${role}' offset must be [x, y] CSS value strings, e.g. ["-30px", "-20%"]`)
        }
      }
      if (config.height !== undefined) {
        if (typeof config.height !== 'string' || config.height.trim().length === 0) {
          throw new Error(`manifest.json "char-config" entry '${role}' height must be a CSS value string, e.g. "80vh" or "86%"`)
        }
      }
    }
  }
  return raw as Manifest
}

// Get the physical file path for a standard role
export function getRoleImagePath(role: StandardRole, theme: string): string {
  const manifest = loadManifest(theme)
  const filename = manifest.roles[role]
  if (!filename) {
    throw new Error(`role '${role}' not found in manifest.json`)
  }
  return path.join(getThemeDir(theme), filename)
}

// Get all standard role → file path mappings
export function getAllRoleImagePaths(theme: string): Record<StandardRole, string> {
  const manifest = loadManifest(theme)
  const baseDir = getThemeDir(theme)
  const result = {} as Record<StandardRole, string>
  for (const role of Object.keys(manifest.roles) as StandardRole[]) {
    result[role] = path.join(baseDir, manifest.roles[role])
  }
  return result
}

// Get the physical file paths for the declared color-source entries.
// These are the ONLY images the token pipeline samples from.
// 条目可以是角色 key（manifest.roles 里查表），也可以是主题目录内的文件名。
export function getColorSourceImagePaths(theme: string): string[] {
  const manifest = loadManifest(theme)
  const themeDir = getThemeDir(theme)
  return manifest.colorSource.map((entry) => {
    const roleFile = manifest.roles[entry as StandardRole]
    if (roleFile) {
      return path.join(themeDir, roleFile)
    }
    const filePath = path.join(themeDir, entry)
    if (!fs.existsSync(filePath)) {
      throw new Error(
        `colorSource entry '${entry}' is neither a role in manifest.json roles nor an existing file in ${themeDir}`
      )
    }
    return filePath
  })
}
