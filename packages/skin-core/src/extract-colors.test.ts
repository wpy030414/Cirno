// packages/skin-core/src/extract-colors.test.ts
// 取色管线通用测试（跨平台）：确定性、色相方向、亮度排序、
// colorSource 纪律与 token schema 完整性——守护 取色→换色 链路。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'
import { extractColors } from './extract-colors.js'
import { buildTokens } from './index.js'
import { getColorSourceImagePaths } from './assets-loader.js'
import { TOKEN_KEYS } from './types.js'
import type { ColorCluster } from './types.js'

const __dirname = path.dirname(fileURLToPath(import.meta.url))

async function gradientPng(file: string, stops: Array<[number, string]>): Promise<void> {
  const svg =
    '<svg width="80" height="80" xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">' +
    stops.map(([o, c]) => '<stop offset="' + o + '%" stop-color="' + c + '"/>').join('') +
    '</linearGradient></defs><rect width="80" height="80" fill="url(#g)"/></svg>'
  await sharp(Buffer.from(svg)).png().toFile(file)
}

/** count 加权平均 RGB */
function weightedMeanRgb(clusters: ColorCluster[]): [number, number, number] {
  let r = 0, g = 0, b = 0, n = 0
  for (const c of clusters) {
    r += c.color[0] * c.count; g += c.color[1] * c.count; b += c.color[2] * c.count; n += c.count
  }
  return [r / n, g / n, b / n]
}

const WARM: Array<[number, string]> = [[0, '#e05a2a'], [50, '#f2a24b'], [100, '#f7d98a']]
const COOL: Array<[number, string]> = [[0, '#2a4ad0'], [50, '#3f9ce0'], [100, '#9ad4f7']]

test('确定性：同一输入两次提取结果完全一致（合成图 + 真实素材）', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'extract-test-'))
  const png = path.join(dir, 'warm.png')
  await gradientPng(png, WARM)
  const a = await extractColors(png, 8)
  const b = await extractColors(png, 8)
  assert.deepEqual(b, a, '同图两次提取必须一致（k-means++ 确定性 PRNG）')
  const maid = path.resolve(__dirname, '../../skin-assets/maid-atelier.theme/maid-atelier-maid-left-v5.webp')
  if (fs.existsSync(maid)) {
    const m1 = await extractColors(maid, 16)
    const m2 = await extractColors(maid, 16)
    assert.deepEqual(m2, m1, '真实素材两次提取必须一致')
  }
  fs.rmSync(dir, { recursive: true, force: true })
})

test('色相方向：暖色图主导 R>B；冷色图主导 B>R', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'extract-test-'))
  const warm = path.join(dir, 'warm.png')
  const cool = path.join(dir, 'cool.png')
  await gradientPng(warm, WARM)
  await gradientPng(cool, COOL)
  const w = weightedMeanRgb(await extractColors(warm, 8))
  const c = weightedMeanRgb(await extractColors(cool, 8))
  assert.ok(w[0] > w[2], '暖色图主导色应偏红（R>B），实际 ' + w.join(','))
  assert.ok(c[2] > c[0], '冷色图主导色应偏蓝（B>R），实际 ' + c.join(','))
  fs.rmSync(dir, { recursive: true, force: true })
})

test('亮度排序：clusters 按 L* 升序（暗→亮）', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'extract-test-'))
  const png = path.join(dir, 'mixed.png')
  await gradientPng(png, [[0, '#101010'], [33, '#808080'], [66, '#f0f0f0'], [100, '#ffffff']])
  const clusters = await extractColors(png, 8)
  for (let i = 1; i < clusters.length; i++) {
    assert.ok(clusters[i].brightness >= clusters[i - 1].brightness, 'clusters 应按亮度升序')
  }
  fs.rmSync(dir, { recursive: true, force: true })
})

test('buildTokens：确定性 + light/dark 各 46 键 + 全部 6 位 hex', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'extract-test-'))
  const png = path.join(dir, 'warm.png')
  await gradientPng(png, WARM)
  const t1 = await buildTokens({ sources: [png], k: 8 })
  const t2 = await buildTokens({ sources: [png], k: 8 })
  assert.deepEqual(t2, t1, 'buildTokens 两次构建必须一致')
  assert.equal(TOKEN_KEYS.length, 46, 'token schema 应为 46 键')
  const hex6 = /^#[0-9a-fA-F]{6}$/
  for (const mode of ['light', 'dark'] as const) {
    const keys = Object.keys(t1[mode]).sort()
    assert.deepEqual(keys, [...TOKEN_KEYS].sort(), mode + ' 模式键集合必须与 TOKEN_KEYS 一致')
    for (const [k, v] of Object.entries(t1[mode])) {
      assert.ok(hex6.test(v), mode + '.' + k + ' 应为 6 位 hex，实际 ' + v)
    }
  }
  fs.rmSync(dir, { recursive: true, force: true })
})

test('colorSource 纪律：仅 manifest colorSource 图参与取色（背景毒色不进 tokens）', async () => {
  const themeDir = path.resolve(__dirname, '../../skin-assets/.tmp-extract-test.theme')
  fs.mkdirSync(themeDir, { recursive: true })
  try {
    const warmPath = path.join(themeDir, 'warm.png')
    const poisonPath = path.join(themeDir, 'poison.png')
    await gradientPng(warmPath, WARM)
    await gradientPng(poisonPath, [[0, '#00ff00'], [100, '#00ff00']]) // 纯毒绿
    fs.writeFileSync(
      path.join(themeDir, 'manifest.json'),
      JSON.stringify({
        schema: 'standard-roles-v1',
        roles: {
          'background-day': 'poison.png',
          'background-night': 'warm.png',
          'character-left': 'warm.png',
          'character-right': 'warm.png',
        },
        colorSource: ['warm.png'],
      }, null, 2)
    )
    const sources = getColorSourceImagePaths('.tmp-extract-test')
    assert.deepEqual(sources, [warmPath], 'colorSource 应只解析出声明文件（不含场景背景）')
    const tokens = await buildTokens({ sources, k: 8 })
    const allValues = [...Object.values(tokens.light), ...Object.values(tokens.dark)]
    assert.ok(!allValues.includes('#00ff00'), '未声明的背景图颜色不得进入 tokens')
    assert.ok(allValues.length === 92, 'tokens 应为 46×2')
  } finally {
    fs.rmSync(themeDir, { recursive: true, force: true })
  }
})
