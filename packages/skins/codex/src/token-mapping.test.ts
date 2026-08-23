// src/token-mapping.test.ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  contrastRatio,
  generateChromeThemeColors,
  generateChromeThemeToml,
  generateThemeV1String,
  generateTokenMappingCSS,
  withAlpha,
  type TokenPairs,
} from './token-mapping.js'
import { fixtureTokens } from './fixture.js'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const HEX_RE = /^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/

const tokens: TokenPairs = { ...fixtureTokens(), 'char-config': { 'character-left': { offset: ['0%', '0%'], height: '86%' }, 'character-right': { offset: ['-30px', '-20%'], height: '78%' } } }

test('generateTokenMappingCSS: 三段结构 + 全部值合法 + 无 undefined', () => {
  const css = generateTokenMappingCSS(tokens)
  assert.match(css, /html\[data-skin\] \{/)
  assert.match(css, /html\[data-skin\]\.electron-light \{/)
  assert.match(css, /html\[data-skin\]\.electron-dark \{/)
  assert.ok(!css.includes('undefined'), 'CSS 不得包含 undefined')
  const lines = css.split('\n')
  const valueLines = lines.filter((l) => /^\s+--[a-z0-9-]+:\s/.test(l))
  assert.ok(valueLines.length > 150, '映射条数应 >150，实际 ' + valueLines.length)
  for (const line of valueLines) {
    // char-config 的变量值是 CSS 长度（86% / calc(...)），单独豁免
    if (line.includes('--character-')) continue
    const value = line.split(':')[1].trim().replace(/;$/, '')
    const ok = HEX_RE.test(value) || /^var\(--/.test(value) || /^#FFFFFF$/i.test(value) || /calc\(/.test(value)
    assert.ok(ok, '非法映射值: ' + line)
  }
})

test('generateTokenMappingCSS: 关键目标变量存在（surface/text/accent/semantic）', () => {
  const css = generateTokenMappingCSS(tokens)
  for (const target of [
    '--color-token-main-surface-primary',
    '--color-token-side-bar-background',
    '--color-token-foreground',
    '--color-token-link',
    '--color-token-primary',
    '--color-token-border',
    '--color-token-error-foreground',
    '--color-token-diff-editor-inserted-line-background',
    '--oai-wb-surface-primary',
    '--oai-wb-text-primary',
    '--oai-wb-accent',
    '--oai-wb-entity-accent',
    '--oai-wb-control-border',
    '--oai-wb-on-accent',
    '--oai-wb-interactive-background',
    '--composer-top-tray-background',
    '--mermaid-surface-background',
    '--vscode-terminal-background',
    '--color-token-text-code-block-background',
    '--color-token-text-link-foreground',
    '--color-token-button-secondary-hover-background',
    '--color-token-activity-bar-badge-foreground',
    '--main-surface-secondary',
    '--text-primary',
    '--border-medium',
    '--border-light',
    '--shimmer-text-secondary',
    '--referral-rate-limit-surface',
    '--referral-rate-limit-muted-surface',
    '--referral-rate-limit-input-background',
    '--referral-rate-limit-border',
    '--referral-rate-limit-check-background',
    '--referral-rate-limit-success-background',
    '--referral-rate-limit-tracking-background',
    '--referral-rate-limit-tracking-hover-background',
    '--character-left-position',
  ]) {
    assert.ok(css.includes(target + ':'), '缺少目标变量 ' + target)
  }
})

test('chromeTheme 颜色：ink/surface ≥4.5、accent/diffAdded/diffRemoved/skill ≥3（双套）', () => {
  const c = generateChromeThemeColors(tokens)
  for (const mode of ['light', 'dark'] as const) {
    const colors = c[mode]
    assert.ok(contrastRatio(colors.ink, colors.surface) >= 4.5, mode + ' ink:surface 对比度不足')
    assert.ok(contrastRatio(colors.accent, colors.surface) >= 3, mode + ' accent:surface 对比度不足')
    assert.ok(contrastRatio(colors.diffAdded, colors.surface) >= 3, mode + ' diffAdded 对比度不足')
    assert.ok(contrastRatio(colors.diffRemoved, colors.surface) >= 3, mode + ' diffRemoved 对比度不足')
    assert.ok(contrastRatio(colors.skill, colors.surface) >= 3, mode + ' skill 对比度不足')
  }
})

test('generateChromeThemeToml: 段结构与 6 位 hex', () => {
  const toml = generateChromeThemeToml(tokens)
  assert.match(toml, /\[desktop\.appearanceLightChromeTheme\]/)
  assert.match(toml, /\[desktop\.appearanceDarkChromeTheme\]/)
  assert.match(toml, /\[desktop\.appearanceLightChromeTheme\.semanticColors\]/)
  for (const key of ['accent', 'ink', 'surface', 'diffAdded', 'diffRemoved', 'skill']) {
    const count = (toml.match(new RegExp(key + ' = ', 'g')) || []).length
    assert.equal(count, 2, key + ' 应出现 2 次（light+dark）')
  }
  const colors = toml.match(/#[0-9a-fA-F]{6}/g) || []
  assert.equal(colors.length, 12, '应有 12 个 6 位 hex（2 套 × 6 键）')
})

test('generateThemeV1String: 官方导入串可解析', () => {
  for (const mode of ['light', 'dark'] as const) {
    const s = generateThemeV1String(tokens, mode)
    assert.ok(s.startsWith('codex-theme-v1:{'), s)
    const payload = JSON.parse(s.slice('codex-theme-v1:'.length))
    assert.equal(payload.variant, mode)
    assert.ok(payload.theme.accent && payload.theme.ink && payload.theme.surface)
    assert.ok(payload.theme.semanticColors.diffAdded)
  }
})

test('skin.css 不硬编码颜色（仓库规则）', () => {
  const css = fs.readFileSync(path.resolve(__dirname, './skin.css'), 'utf8')
  assert.ok(!/#[0-9a-fA-F]{3,8}/.test(css), 'skin.css 出现硬编码颜色')
  assert.ok(!/rgb\(|rgba\(|hsl\(/.test(css), 'skin.css 出现硬编码颜色函数')
})

test('withAlpha: 6 位 hex → 8 位带 alpha；非法输入原样返回', () => {
  assert.equal(withAlpha('#123456', 0.5), '#12345680')
  assert.equal(withAlpha('#123456', 1), '#123456ff')
  assert.equal(withAlpha('var(--x)', 0.5), 'var(--x)')
})
