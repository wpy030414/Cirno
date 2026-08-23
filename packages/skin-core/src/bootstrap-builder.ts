// src/bootstrap-builder.ts
// Shared bootstrap building logic for all skin packages.
// Generates the HTML fragment (inline CSS + image data URIs + inject.js)
// that gets injected into the target app's index.html (before </head>).
import fs from 'node:fs'
import path from 'node:path'
import sharp from 'sharp'
import { getAllRoleImagePaths, getActiveTheme } from './assets-loader.js'

// Chromium 对 URL 有 2MB 硬上限（kMaxURLChars），超长会被静默替换为无效 URL。
// data URI 也是 URL——内联前必须保证每张图 base64 后明显低于该上限。
const DATA_URI_SAFE_LIMIT = 1.5 * 1024 * 1024

/**
 * Read an image file and convert it to a data URI (without the url() wrapper).
 * 不做压缩——需要压缩请用 imageFileToOptimizedDataUri()。
 */
export function imageFileToDataUri(filePath: string): string {
  const buf = fs.readFileSync(filePath)
  const ext = path.extname(filePath).toLowerCase().slice(1)
  const mime = ext === 'jpg' ? 'jpeg' : ext
  return `data:image/${mime};base64,${buf.toString('base64')}`
}

/**
 * 图片 → data URI（构建时压缩）：
 * - 小图（base64 后 < DATA_URI_SAFE_LIMIT）原样内联，零质量损失
 * - 大图用 sharp 缩放（上限 1920×1080，不放大）+ 转 webp（quality 82）再内联，
 *   保证低于 Chromium 的 2MB URL 上限，否则背景图会被静默丢弃
 * - 压缩后反而更大时退回原图
 */
export async function imageFileToOptimizedDataUri(filePath: string): Promise<string> {
  const raw = imageFileToDataUri(filePath)
  if (raw.length < DATA_URI_SAFE_LIMIT) {
    return raw
  }
  const optimized = await sharp(filePath)
    .resize({ width: 1920, height: 1080, fit: 'inside', withoutEnlargement: true })
    .webp({ quality: 82 })
    .toBuffer()
  const uri = `data:image/webp;base64,${optimized.toString('base64')}`
  return uri.length < raw.length ? uri : raw
}

/**
 * Build the JavaScript snippet that injects image data URIs as CSS custom properties.
 * Uses DELAYED LOADING (setTimeout 1ms) to avoid triggering OOM watchdogs in some Electron apps.
 * 主题不在此选择——跟随 build-tokens 选定的活动主题（dist/tokens.json 的 theme 字段）。
 * 大图经 sharp 压缩转 webp（见 imageFileToOptimizedDataUri），保证 < Chromium 2MB URL 上限。
 */
export async function buildImageInjectionScript(): Promise<string> {
  const theme = getActiveTheme()
  const rolePaths = getAllRoleImagePaths(theme)
  const ART_FILES: Array<{ var: string; src: string; delayMs: number }> = [
    { var: '--background-day', src: rolePaths['background-day'], delayMs: 1 },
    { var: '--background-night', src: rolePaths['background-night'], delayMs: 1 },
    { var: '--character-left', src: rolePaths['character-left'], delayMs: 1 },
    { var: '--character-right', src: rolePaths['character-right'], delayMs: 1 },
  ]

  let script = ';(function(){\n'
  script += '  var skinImages = window.__skinImages = {};\n'

  for (const { var: varName, src } of ART_FILES) {
    if (!fs.existsSync(src)) throw new Error(`missing asset: ${src}`)
    const uri = await imageFileToOptimizedDataUri(src)
    script += `  skinImages['${varName}'] = '${uri}';\n`
  }

  script += '  function applyImage(varName) {\n'
  script += '    if (!skinImages[varName]) return;\n'
  script += '    document.documentElement.style.setProperty(varName, "url(" + skinImages[varName] + ")");\n'
  script += '    delete skinImages[varName];\n'
  script += '  }\n'

  for (const { var: varName, delayMs } of ART_FILES) {
    script += `  setTimeout(function(){ applyImage('${varName}'); }, ${delayMs});\n`
  }

  script += '})();\n'
  return script
}

/**
 * The shared bootstrap body (data-skin setter + image injection + inject.js).
 */
export async function buildBootstrapBody(injectJs: string): Promise<string> {
  return (
    `;(function(){\n` +
    `  document.documentElement.dataset.skin = "active"\n` +
    `})();\n` +
    (await buildImageInjectionScript()) +
    injectJs +
    `\n`
  )
}

/**
 * Build the complete bootstrap HTML fragment.
 * Produces inline <style> + <script> tags — injected into the target app's
 * index.html before </head>.
 */
export async function buildBootstrap(params: {
  css: string
  injectJs: string
  marker: string
}): Promise<string> {
  const { css, injectJs, marker } = params
  return (
    `\n<!-- ${marker} start -->\n` +
    `<style id="${marker}-style">${css}</style>\n` +
    `<script id="${marker}-script">\n` +
    (await buildBootstrapBody(injectJs)) +
    `\n</script>\n` +
    `<!-- ${marker} end -->\n`
  )
}

/**
 * Inject the bootstrap fragment into an HTML document, before </head>.
 */
export function injectBootstrapIntoHtml(html: string, bootstrap: string): string {
  if (!html.includes('</head>')) {
    throw new Error('</head> not found in index.html — cannot inject bootstrap')
  }
  return html.replace('</head>', bootstrap + '</head>')
}
