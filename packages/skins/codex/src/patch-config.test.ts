// src/patch-config.test.ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import {
  MARKER,
  applySectionColors,
  hasMarker,
  patchConfigFile,
  restoreConfigFile,
  setAppearanceThemePin,
} from './patch-config.js'
import type { ChromeThemeColors } from './token-mapping.js'

const LIGHT: ChromeThemeColors = { accent: '#aaaa01', ink: '#111111', surface: '#f0f0f0', diffAdded: '#00aa00', diffRemoved: '#aa0000', skill: '#0000aa' }
const DARK: ChromeThemeColors = { accent: '#bbbb02', ink: '#eeeeee', surface: '#101010', diffAdded: '#00bb00', diffRemoved: '#bb0000', skill: '#0000bb' }

const BASE = [
  'model = "gpt-5.6-sol"',
  '[desktop]',
  'followUpQueueMode = "steer"',
  'sansFontSize = 15',
  '',
  '[desktop.appearanceLightChromeTheme]',
  'accent = "#93b259"',
  'contrast = 45',
  'ink = "#5c6a72"',
  'opaqueWindows = false',
  'surface = "#fdf6e3"',
  '',
  '[desktop.appearanceLightChromeTheme.fonts]',
  '',
  '[desktop.appearanceLightChromeTheme.semanticColors]',
  'diffAdded = "#8da101"',
  'diffRemoved = "#f85552"',
  'skill = "#df69ba"',
  '',
  "[projects.'c:\\users\\inagn\\codex\\x']",
  '',
  '[plugins."browser@openai-bundled"]',
  'enabled = true',
].join('\n') + '\n'

function tmpDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'codex-skin-test-'))
}

test('首次 apply：只改 chromeTheme 颜色键，段外字节零改动，marker 幂等', () => {
  const dir = tmpDir()
  const cfg = path.join(dir, 'config.toml')
  fs.writeFileSync(cfg, BASE)
  const result = patchConfigFile(cfg, LIGHT, DARK)
  const after = fs.readFileSync(cfg, 'utf8')
  assert.equal(result.backedUp, true)
  assert.ok(fs.existsSync(cfg + '.skin.bak'))
  assert.ok(after.includes('accent = "#aaaa01"'), 'light accent 未替换')
  assert.ok(after.includes('accent = "#bbbb02"'), 'dark accent 未创建')
  assert.ok(after.includes('contrast = 45'), 'contrast（用户键）被改动')
  assert.ok(after.includes('opaqueWindows = false'), 'opaqueWindows（用户键）被改动')
  assert.ok(after.includes('[desktop.appearanceLightChromeTheme.fonts]'), 'fonts 段丢失')
  assert.ok(after.includes('sansFontSize = 15'), 'desktop 其他键被改动')
  assert.ok(after.includes('enabled = true'), 'plugins 段被改动')
  assert.ok(hasMarker(after.split(/\r?\n/)), 'marker 缺失')
  // 段外零改动：过滤受管行（marker / 四个段 header / 颜色键）后，非空行序列一致
  const strip = (s: string) =>
    s.split(/\r?\n/).filter((l) => {
      if (l.includes('codex-skin-marker')) return false
      if (/^\[desktop\.appearance(Light|Dark)ChromeTheme(\.semanticColors)?\]$/.test(l.trim())) return false
      if (/^(accent|ink|surface|diffAdded|diffRemoved|skill)\s*=/.test(l)) return false
      return true
    }).filter((l) => l.trim() !== '')
  assert.deepEqual(strip(after), strip(BASE), '受管段之外存在内容差异')
  // 幂等：二次 apply 无 --force 报错
  assert.throws(() => patchConfigFile(cfg, LIGHT, DARK), /already patched/)
  fs.rmSync(dir, { recursive: true, force: true })
})

test('--force：从 pristine 备份还原再打；备份被污染则拒绝', () => {
  const dir = tmpDir()
  const cfg = path.join(dir, 'config.toml')
  fs.writeFileSync(cfg, BASE)
  patchConfigFile(cfg, LIGHT, DARK)
  // 用户在此期间改了自己的配置
  fs.writeFileSync(cfg, fs.readFileSync(cfg, 'utf8') + 'user_noise = 1\n')
  const result = patchConfigFile(cfg, LIGHT, DARK, { force: true })
  assert.equal(result.restoredFromBackup, true)
  const after = fs.readFileSync(cfg, 'utf8')
  assert.ok(!after.includes('user_noise'), '--force 应从 pristine 备份还原')
  assert.ok(after.includes('accent = "#aaaa01"'))
  // 污染备份 → 拒绝
  fs.appendFileSync(cfg + '.skin.bak', MARKER + '\n')
  assert.throws(() => patchConfigFile(cfg, LIGHT, DARK, { force: true }), /contaminated/)
  fs.rmSync(dir, { recursive: true, force: true })
})

test('--no-backup 仍保留 pristine snapshot；还原完全精确', () => {
  const dir = tmpDir()
  const cfg = path.join(dir, 'config.toml')
  fs.writeFileSync(cfg, BASE)
  patchConfigFile(cfg, LIGHT, DARK, { noBackup: true })
  const bak = cfg + '.skin.bak'
  assert.ok(fs.existsSync(bak), '--no-backup 也应保留 pristine snapshot')
  restoreConfigFile(cfg, bak)
  assert.equal(fs.readFileSync(cfg, 'utf8'), BASE, '还原后应与原始字节一致')
  assert.ok(!fs.readFileSync(cfg, 'utf8').includes('codex-skin-marker'))
  fs.rmSync(dir, { recursive: true, force: true })
})

test('--pin：写/覆盖 [desktop] 的 appearanceTheme', () => {
  const dir = tmpDir()
  const cfg = path.join(dir, 'config.toml')
  fs.writeFileSync(cfg, BASE)
  patchConfigFile(cfg, LIGHT, DARK, { pin: 'light' })
  const after = fs.readFileSync(cfg, 'utf8')
  assert.ok(after.includes('appearanceTheme = "light"'))
  patchConfigFile(cfg, LIGHT, DARK, { force: true, pin: 'dark' })
  const after2 = fs.readFileSync(cfg, 'utf8')
  assert.ok(after2.includes('appearanceTheme = "dark"'))
  assert.ok(!after2.includes('appearanceTheme = "light"'))
  fs.rmSync(dir, { recursive: true, force: true })
})

test('CRLF 保留：CRLF 输入 → CRLF 输出；LF 输入 → LF 输出', () => {
  const dir = tmpDir()
  const cfgCrlf = path.join(dir, 'config-crlf.toml')
  fs.writeFileSync(cfgCrlf, BASE.replace(/\n/g, '\r\n'))
  patchConfigFile(cfgCrlf, LIGHT, DARK)
  const outCrlf = fs.readFileSync(cfgCrlf, 'utf8')
  assert.ok(outCrlf.includes('\r\n'), 'CRLF 未保留')
  assert.ok(!outCrlf.split('\r\n').some((l) => l.endsWith('\n')), '出现混合行尾')
  const cfgLf = path.join(dir, 'config-lf.toml')
  fs.writeFileSync(cfgLf, BASE)
  patchConfigFile(cfgLf, LIGHT, DARK)
  const outLf = fs.readFileSync(cfgLf, 'utf8')
  assert.ok(!outLf.includes('\r'), 'LF 输入被转成 CRLF')
  fs.rmSync(dir, { recursive: true, force: true })
})

test('纯函数：applySectionColors / setAppearanceThemePin 不改变输入数组', () => {
  const lines = BASE.split('\n')
  const snapshot = [...lines]
  applySectionColors(lines, '[desktop.appearanceLightChromeTheme]', { accent: '#ff0000' })
  setAppearanceThemePin(lines, 'dark')
  assert.deepEqual(lines, snapshot, '纯函数不得修改输入')
})
