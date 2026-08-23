// src/patch-config.ts
// config.toml 补丁引擎（官方 chrome 层，辅通道）：
//   - 行级段编辑：只改 [desktop.appearanceLightChromeTheme] / [desktop.appearanceDarkChromeTheme]
//     及其 .semanticColors 子段的颜色键；段外字节零改动；
//     contrast / opaqueWindows / fonts 等用户键一律保留
//   - marker 幂等检测；--force 从 .skin.bak（pristine）还原再打
//   - .skin.bak 双重身份：回滚副本 + pristine source（DECISIONS D-010）
//   - --pin light|dark 仅测试用：写 [desktop] 的 appearanceTheme；还原时随 .bak 覆盖消失
import fs from 'node:fs'
import type { ChromeThemeColors } from './token-mapping.js'

export const MARKER = '# codex-skin-marker'

export interface PatchConfigOptions {
  force?: boolean
  noBackup?: boolean
  pin?: 'light' | 'dark' | null
}

export interface PatchConfigResult {
  configPath: string
  backupPath: string
  backedUp: boolean
  restoredFromBackup: boolean
}

export function findSection(lines: string[], header: string): { start: number; end: number } {
  const start = lines.findIndex((l) => l.trim() === header)
  if (start === -1) return { start: -1, end: -1 }
  let end = lines.length
  for (let i = start + 1; i < lines.length; i++) {
    if (lines[i].trim().startsWith('[')) {
      end = i
      break
    }
  }
  return { start, end }
}

export function hasMarker(lines: string[]): boolean {
  return lines.some((l) => l.includes('codex-skin-marker'))
}

/** 在指定段内替换/补充颜色键（无副作用的纯函数） */
export function applySectionColors(lines: string[], header: string, colors: Record<string, string>): string[] {
  const { start, end } = findSection(lines, header)
  const body = start === -1 ? [] : lines.slice(start + 1, end)
  const keyOf = (line: string): string | null => {
    const m = line.match(/^\s*([A-Za-z0-9_-]+)\s*=/);
    return m ? m[1] : null
  }
  const next: string[] = []
  for (const line of body) {
    const key = keyOf(line)
    if (key && Object.prototype.hasOwnProperty.call(colors, key)) {
      next.push(key + ' = "' + colors[key] + '"')
      continue
    }
    next.push(line)
  }
  for (const key of Object.keys(colors)) {
    if (!next.some((l) => keyOf(l) === key)) next.push(key + ' = "' + colors[key] + '"')
  }
  const section = [header, MARKER, ...next]
  if (start === -1) {
    const result = [...lines]
    if (result.length > 0 && result[result.length - 1].trim() !== '') result.push('')
    result.push(...section)
    return result
  }
  return [...lines.slice(0, start), ...section, ...lines.slice(end)]
}

/** 写/覆盖 [desktop] 段的 appearanceTheme（仅测试用 pin） */
export function setAppearanceThemePin(lines: string[], pin: 'light' | 'dark'): string[] {
  const { start, end } = findSection(lines, '[desktop]')
  const body = start === -1 ? [] : lines.slice(start + 1, end)
  let replaced = false
  const next = body.map((l) => {
    if (/^\s*appearanceTheme\s*=/.test(l)) {
      replaced = true
      return 'appearanceTheme = "' + pin + '"'
    }
    return l
  })
  if (!replaced) next.push('appearanceTheme = "' + pin + '"')
  if (start === -1) return [...lines, '', '[desktop]', ...next]
  return [...lines.slice(0, start + 1), ...next, ...lines.slice(end)]
}

function readConfig(configPath: string): { raw: string; eol: string; bom: string } {
  const raw = fs.readFileSync(configPath, 'utf8')
  return {
    raw,
    eol: raw.includes('\r\n') ? '\r\n' : '\n',
    bom: raw.startsWith('\uFEFF') ? '\uFEFF' : ''
  }
}

function writeConfig(configPath: string, lines: string[], eol: string, bom: string): void {
  const text = bom + lines.join(eol) + (lines.length > 0 ? eol : '')
  fs.writeFileSync(configPath, text, 'utf8')
}

export function patchConfigFile(
  configPath: string,
  light: ChromeThemeColors,
  dark: ChromeThemeColors,
  options: PatchConfigOptions = {}
): PatchConfigResult {
  const backupPath = configPath + '.skin.bak'
  const backupExists = fs.existsSync(backupPath)
  if (!fs.existsSync(configPath)) {
    throw new Error('config.toml not found: ' + configPath)
  }

  let { raw, eol, bom } = readConfig(configPath)
  let lines = raw.split(/\r?\n/)
  let restoredFromBackup = false

  if (hasMarker(lines)) {
    if (!options.force) {
      throw new Error('already patched (marker found) — re-run with --force to re-apply from the pristine backup')
    }
    if (!backupExists) {
      throw new Error('--force requires a pristine backup (.skin.bak), but none exists')
    }
    const backup = readConfig(backupPath)
    const backupLines = backup.raw.split(/\r?\n/)
    if (hasMarker(backupLines)) {
      throw new Error('backup is contaminated (contains marker) — refusing to patch from it')
    }
    lines = backupLines
    eol = backup.eol
    bom = backup.bom
    restoredFromBackup = true
  }

  // .bak 双重身份：不存在时必建（即使 --no-backup 也保留 pristine snapshot）
  if (!backupExists) {
    fs.copyFileSync(configPath, backupPath)
  }

  lines = applySectionColors(lines, '[desktop.appearanceLightChromeTheme]', {
    accent: light.accent,
    ink: light.ink,
    surface: light.surface,
  })
  lines = applySectionColors(lines, '[desktop.appearanceLightChromeTheme.semanticColors]', {
    diffAdded: light.diffAdded,
    diffRemoved: light.diffRemoved,
    skill: light.skill,
  })
  lines = applySectionColors(lines, '[desktop.appearanceDarkChromeTheme]', {
    accent: dark.accent,
    ink: dark.ink,
    surface: dark.surface,
  })
  lines = applySectionColors(lines, '[desktop.appearanceDarkChromeTheme.semanticColors]', {
    diffAdded: dark.diffAdded,
    diffRemoved: dark.diffRemoved,
    skill: dark.skill,
  })
  if (options.pin) lines = setAppearanceThemePin(lines, options.pin)

  writeConfig(configPath, lines, eol, bom)
  return { configPath, backupPath, backedUp: !backupExists, restoredFromBackup }
}

/** 完全还原：.skin.bak 覆盖回 config.toml（与仓库「.bak 覆盖回 app.asar」同一语义） */
export function restoreConfigFile(configPath: string, backupPath: string): void {
  if (!fs.existsSync(backupPath)) {
    throw new Error('backup not found: ' + backupPath)
  }
  const backup = readConfig(backupPath)
  if (hasMarker(backup.raw.split(/\r?\n/))) {
    throw new Error('backup is contaminated (contains marker) — refusing to restore from it')
  }
  fs.copyFileSync(backupPath, configPath)
}
