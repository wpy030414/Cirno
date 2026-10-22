// packages/skin-core/src/palette-preview.ts
// Generate an HTML preview of the extracted color palette.
import fs from 'node:fs'
import path from 'node:path'
import { extractColorsFromImages } from './extract-colors.js'
import { generateTokenPairs, rgbToHex } from './generate-tokens.js'
import { getColorSourceImagePaths, getActiveTheme } from './assets-loader.js'

async function main() {
  const args = process.argv.slice(2)

  let sources: string[] = []
  let outPath = 'dist/palette.html'

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--sources' && i + 1 < args.length) {
      sources = args[i + 1].split(',')
      i++
    } else if (args[i] === '--out' && i + 1 < args.length) {
      outPath = args[i + 1]
      i++
    }
  }

  // If no --sources provided, follow the active theme (selected by build-tokens)
  // and load its manifest colorSource (character art only — scene backgrounds must NOT tint the theme)
  if (sources.length === 0) {
    const theme = getActiveTheme()
    sources = getColorSourceImagePaths(theme)
    console.log(`   Using active theme '${theme}' colorSource (${sources.length} images)`)
  }

  console.log('🎨 Generating palette preview...')

  // Extract colors (colorSource images only)
  const clusters = await extractColorsFromImages(sources, 16)
  const { light, dark } = generateTokenPairs(clusters)

  // Generate HTML
  const html = `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Skin Palette Preview</title>
  <style>
    body {
      font-family: system-ui, -apple-system, sans-serif;
      padding: 2rem;
      background: #f5f5f5;
    }
    h1 { margin-top: 0; }
    .palette {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 1rem;
      margin: 2rem 0;
    }
    .color-card {
      background: white;
      border-radius: 8px;
      overflow: hidden;
      box-shadow: 0 2px 8px rgba(0,0,0,0.1);
    }
    .color-swatch {
      height: 120px;
      width: 100%;
    }
    .color-info {
      padding: 1rem;
    }
    .color-name {
      font-weight: 600;
      margin: 0 0 0.5rem 0;
    }
    .color-value {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.875rem;
      color: #666;
      margin: 0;
    }
    .tokens {
      background: white;
      padding: 1.5rem;
      border-radius: 8px;
      margin-top: 2rem;
    }
    .token-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(250px, 1fr));
      gap: 1rem;
      margin-top: 1rem;
    }
    .token-item {
      display: flex;
      align-items: center;
      gap: 0.75rem;
    }
    .token-swatch {
      width: 40px;
      height: 40px;
      border-radius: 6px;
      border: 1px solid rgba(0,0,0,0.1);
    }
    .token-name {
      font-family: 'SF Mono', Monaco, monospace;
      font-size: 0.875rem;
    }
  </style>
</head>
<body>
  <h1>🎨 Skin Palette Preview</h1>
  <p>Extracted from ${sources.length} source images</p>

  <h2>Color Clusters</h2>
  <div class="palette">
    ${clusters
      .map(
        (cluster) => `
      <div class="color-card">
        <div class="color-swatch" style="background: ${rgbToHex(cluster.color)}"></div>
        <div class="color-info">
          <p class="color-name">Cluster (L*: ${cluster.brightness.toFixed(1)})</p>
          <p class="color-value">${rgbToHex(cluster.color)}</p>
          <p class="color-value">${cluster.count} pixels</p>
        </div>
      </div>
    `
      )
      .join('')}
  </div>

  <div class="tokens">
    <h2>Design Tokens</h2>
    <h3>Light</h3>
    <div class="token-grid">
      ${Object.entries(light)
        .map(
          ([name, value]) => `
        <div class="token-item">
          <div class="token-swatch" style="background: ${value}"></div>
          <div>
            <div class="token-name">${name}</div>
            <div class="color-value">${value}</div>
          </div>
        </div>
      `
        )
        .join('')}
    </div>
    <h3>Dark</h3>
    <div class="token-grid">
      ${Object.entries(dark)
        .map(
          ([name, value]) => `
        <div class="token-item">
          <div class="token-swatch" style="background: ${value}"></div>
          <div>
            <div class="token-name">${name}</div>
            <div class="color-value">${value}</div>
          </div>
        </div>
      `
        )
        .join('')}
    </div>
  </div>
</body>
</html>`

  const dir = path.dirname(outPath)
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true })
  }
  fs.writeFileSync(outPath, html)

  console.log(`✓ Preview written to ${outPath}`)
  console.log(`  Open in browser: file://${path.resolve(outPath)}`)
}

main().catch((err) => {
  console.error('❌ Preview generation failed:', err.message)
  process.exit(1)
})
