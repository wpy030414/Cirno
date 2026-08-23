// src/contrast.test.ts
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { contrastRatio, generateTokenMappingCSS, luminance, withAlpha } from './token-mapping.js'
import type { TokenPairs } from './token-mapping.js'
import { fixtureTokens } from './fixture.js'

/** 从生成的 CSS 中取某选择器块内某变量的值 */
function valueOf(css: string, selector: string, target: string): string {
  const blockMatch = css.split('}').find((part) => part.includes(selector) && part.includes(target + ':'))
  if (!blockMatch) throw new Error('变量不在块内: ' + selector + ' → ' + target)
  const line = blockMatch.split('\n').find((l) => l.includes(target + ':'))!
  return line.split(':')[1].trim().replace(/;$/, '').slice(0, 7)
}

test('映射后关键表面/文字/强调对对比度达标（light+dark，fixture tokens）', () => {
  const tokens: TokenPairs = fixtureTokens()
  const css = generateTokenMappingCSS(tokens)
  const checks: Array<[string, string, string, number]> = [
    // [mode, 前景变量, 背景变量, 最低比率]
    ['light', '--color-token-foreground', '--color-token-main-surface-primary', 4.5],
    ['dark', '--color-token-foreground', '--color-token-main-surface-primary', 4.5],
    ['light', '--color-token-link', '--color-token-main-surface-primary', 4.5],
    ['dark', '--color-token-link', '--color-token-main-surface-primary', 4.5],
    ['light', '--color-token-error-foreground', '--color-token-main-surface-primary', 3],
    ['dark', '--color-token-error-foreground', '--color-token-main-surface-primary', 3],
    ['light', '--color-token-input-foreground', '--color-token-input-background', 4.5],
    ['dark', '--color-token-input-foreground', '--color-token-input-background', 4.5],
    ['light', '--color-token-editor-foreground', '--color-token-editor-background', 4.5],
    ['dark', '--color-token-editor-foreground', '--color-token-editor-background', 4.5],
    ['light', '--color-token-button-foreground', '--color-token-button-background', 4.5],
    ['light', '--color-token-dropdown-foreground', '--color-token-dropdown-background', 4.5],
    ['dark', '--color-token-dropdown-foreground', '--color-token-dropdown-background', 4.5],
    ['light', '--oai-wb-text-primary', '--oai-wb-surface-primary', 4.5],
    ['dark', '--oai-wb-text-primary', '--oai-wb-surface-primary', 4.5],
    ['dark', '--color-token-button-foreground', '--color-token-button-background', 4.5],
  ]
  for (const [mode, fgVar, bgVar, min] of checks) {
    const selector = 'html[data-skin]' + (mode === 'light' ? '.electron-light' : '.electron-dark')
    const fg = valueOf(css, selector, fgVar)
    const bg = valueOf(css, selector, bgVar)
    const ratio = contrastRatio(fg, bg)
    assert.ok(ratio >= min, mode + ' ' + fgVar + '(' + fg + ') vs ' + bgVar + '(' + bg + ') = ' + ratio.toFixed(2) + ' < ' + min)
  }
})


test('亮度方向回归：暗色弹层变量不得为亮色（白对话框根除），浅色不得为暗色', () => {
  const tokens: TokenPairs = fixtureTokens()
  const css = generateTokenMappingCSS(tokens)
  const targets = [
    '--color-token-dropdown-background',
    '--color-token-menu-background',
    '--color-token-bg-tertiary',
    '--color-token-editor-background',
  ]
  for (const mode of ['light', 'dark'] as const) {
    const selector = 'html[data-skin]' + (mode === 'light' ? '.electron-light' : '.electron-dark')
    for (const target of targets) {
      const value = valueOf(css, selector, target)
      const hex = value.slice(0, 7)
      const lum = luminance(hex)
      if (mode === 'dark') assert.ok(lum < 0.5, 'dark ' + target + ' = ' + value + ' 过亮（白对话框回归）')
      else assert.ok(lum > 0.5, 'light ' + target + ' = ' + value + ' 过暗（黑对话框回归）')
    }
  }
  // 半透明弹层组件：合成到主面后同样必须暗（暗）/亮（浅）
  const blend = (fgHex: string, baseHex: string): string => {
    const a = parseInt(fgHex.slice(7, 9) || 'ff', 16) / 255
    const p = (c: number, b: number) => Math.round(c * a + b * (1 - a))
    const f = (s: string, i: number) => parseInt(s.slice(i, i + 2), 16)
    const out = (i: number) => p(f(fgHex, i), f(baseHex, i)).toString(16).padStart(2, '0')
    return '#' + out(1) + out(3) + out(5)
  }
  for (const mode of ['light', 'dark'] as const) {
    const selector = 'html[data-skin]' + (mode === 'light' ? '.electron-light' : '.electron-dark')
    const base = valueOf(css, selector, '--color-token-main-surface-primary')
    for (const target of ['--color-token-checkbox-background', '--color-token-editor-widget-background']) {
      const v = valueOf(css, selector, target)
      const blended = blend(v, base.slice(0, 7))
      const lum = luminance(blended)
      if (mode === 'dark') assert.ok(lum < 0.5, 'dark ' + target + ' = ' + v + ' 合成后过亮')
      else assert.ok(lum > 0.5, 'light ' + target + ' = ' + v + ' 合成后过暗')
    }
  }
})
test('alpha 叠加值仍保持原 token 对比语义（组件表引用色相）', () => {
  const t = fixtureTokens()
  // diff 行背景：半透明成功/危险色，叠加后与表面仍有足够区分（色相层面断言）
  assert.equal(withAlpha(t.light['success-base'], 0.23).length, 9)
  assert.equal(withAlpha(t.dark['critical-base'], 0.23).length, 9)
})
