// src/patch-asar.ts
// Cross-platform asar patcher for the opencode desktop app.
// Uses extract/repack strategy since OpenCode has no unpacked native module issues.
// Injects inline CSS + data URI images into oc-theme-preload.js.
import fs from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import { fileURLToPath } from 'node:url'
import { execSync, spawn } from 'node:child_process'
import asar from '@electron/asar'
import { buildBootstrap, injectBootstrapIntoHtml } from '@skins/core/bootstrap-builder'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const SRC = __dirname
const ROOT = path.resolve(__dirname, '..')

const MARKER = 'oc-skin'
const RENDERER_HTML = path.join('out', 'renderer', 'index.html')
const FAVICON_ENTRY = path.join('out', 'renderer', 'favicon-v3.svg')

// Cross-platform default app paths
const DEFAULT_APP_DIRS: Record<string, string> = {
  win32: 'C:\\Users\\xrl\\AppData\\Local\\Programs\\@opencode-aidesktop',
  darwin: '/Applications/OpenCode.app',
  linux: '/opt/OpenCode',
}

// Cross-platform executable paths
const APP_EXECUTABLES: Record<string, string> = {
  win32: 'OpenCode.exe',
  darwin: 'Contents/MacOS/OpenCode',
  linux: 'opencode-desktop',
}

interface PatchOptions {
  appDir?: string
  allowRunning?: boolean
  force?: boolean
  noBackup?: boolean
  backupPath?: string
  autoRestart?: boolean
}

function log(msg: string): void {
  console.log('[patch] ' + msg)
}

function fail(msg: string): never {
  console.error('[patch] ERROR: ' + msg)
  process.exit(1)
}

// Stream search for marker in binary file (handles boundary cases)
function hasMarker(filePath: string): boolean {
  const marker = Buffer.from(MARKER, 'utf8')
  const fd = fs.openSync(filePath, 'r')
  try {
    const buf = Buffer.alloc(4 * 1024 * 1024)
    const carry = Buffer.alloc(marker.length - 1)
    let carryLen = 0
    const first = marker[0]
    let bytesRead: number
    while ((bytesRead = fs.readSync(fd, buf, 0, buf.length, null)) > 0) {
      const n = bytesRead + carryLen
      const span = Buffer.alloc(n)
      carry.copy(span, 0, 0, carryLen)
      buf.copy(span, carryLen, 0, bytesRead)
      for (let i = 0; i <= n - marker.length; i++) {
        if (span[i] !== first) continue
        let ok = true
        for (let j = 1; j < marker.length; j++) {
          if (span[i + j] !== marker[j]) {
            ok = false
            break
          }
        }
        if (ok) return true
      }
      const tailLen = marker.length - 1
      if (n >= tailLen) {
        span.copy(carry, 0, n - tailLen, n)
        carryLen = tailLen
      } else {
        span.copy(carry, 0, 0, n)
        carryLen = n
      }
    }
    return false
  } finally {
    fs.closeSync(fd)
  }
}

// Check if OpenCode process is running (cross-platform)
function isOpenCodeRunning(): boolean {
  try {
    const platform = os.platform()
    if (platform === 'win32') {
      const result = execSync('tasklist /FI "IMAGENAME eq OpenCode.exe"', { encoding: 'utf8' })
      return result.includes('OpenCode.exe')
    } else {
      try {
        execSync('pgrep -f OpenCode', { encoding: 'utf8', stdio: 'pipe' })
        return true
      } catch {
        return false
      }
    }
  } catch {
    return false
  }
}

// Kill all OpenCode processes (cross-platform)
function killOpenCode(): void {
  log('Killing OpenCode processes...')
  try {
    const platform = os.platform()
    if (platform === 'win32') {
      execSync('taskkill /F /IM OpenCode.exe', { stdio: 'pipe' })
    } else {
      execSync('pkill -f OpenCode', { stdio: 'pipe' })
    }
    const start = Date.now()
    while (isOpenCodeRunning() && Date.now() - start < 5000) {
      // 100ms synchronous sleep — cross-platform (no shell dependency)
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100)
    }
    log('OpenCode processes terminated')
  } catch {
    log('No OpenCode processes to kill (or kill failed)')
  }
}

// Launch OpenCode (cross-platform)
function launchOpenCode(appDir: string): void {
  const platform = os.platform()
  const exeRelative = APP_EXECUTABLES[platform]
  if (!exeRelative) {
    fail(`unsupported platform: ${platform}`)
  }

  const exePath = path.join(appDir, exeRelative)
  log(`Launching OpenCode from ${exePath}`)

  try {
    if (platform === 'win32') {
      spawn(exePath, [], { detached: true, stdio: 'ignore' }).unref()
    } else if (platform === 'darwin') {
      spawn('open', ['-a', appDir], { detached: true, stdio: 'ignore' }).unref()
    } else {
      spawn(exePath, [], { detached: true, stdio: 'ignore' }).unref()
    }
    log('OpenCode launched')
  } catch (err) {
    log(`Warning: Failed to launch OpenCode: ${err}`)
  }
}

export async function patchAsar(options: PatchOptions = {}): Promise<void> {
  const platform = os.platform()
  const defaultAppDir = DEFAULT_APP_DIRS[platform]
  if (!defaultAppDir) {
    fail(`unsupported platform: ${platform}`)
  }

  const appDir = options.appDir || defaultAppDir
  const resourcesDir = platform === 'darwin'
    ? path.join(appDir, 'Contents', 'Resources')
    : path.join(appDir, 'resources')
  const asarPath = path.join(resourcesDir, 'app.asar')
  const backupPath = options.backupPath || `${asarPath}.skin.bak`
  const force = options.force ?? false
  const noBackup = options.noBackup ?? false
  const allowRunning = options.allowRunning ?? false
  const autoRestart = options.autoRestart ?? true

  if (!fs.existsSync(asarPath)) fail(`app.asar not found: ${asarPath}`)

  if (hasMarker(asarPath) && !force) {
    fail('app.asar already patched - use force:true to re-patch, or restore the .bak')
  }

  if (isOpenCodeRunning()) {
    if (allowRunning) {
      log('OpenCode is running, but allowRunning is set - proceeding anyway')
    } else {
      killOpenCode()
    }
  }

  // Backup
  if (!noBackup) {
    if (fs.existsSync(backupPath)) {
      log(`backup already exists: ${backupPath} (leaving it untouched)`)
    } else {
      fs.copyFileSync(asarPath, backupPath)
      log(`backup: ${backupPath}`)
    }
  } else {
    log('--noBackup: skipping backup; .bak still used as pristine source for --force')
    if (!fs.existsSync(backupPath)) {
      fs.copyFileSync(asarPath, backupPath)
      log(`pristine snapshot for --force: ${backupPath}`)
    }
  }

  // Read static CSS file
  const cssPath = path.join(SRC, 'skin.css')
  if (!fs.existsSync(cssPath)) throw new Error(`skin.css not found in src/`)
  const css = fs.readFileSync(cssPath, 'utf8')

  // Read token-mapping CSS (generated by build-mapping)
  const tokenMappingPath = path.join(SRC, '../dist/token-mapping.css')
  if (!fs.existsSync(tokenMappingPath)) {
    throw new Error(`token-mapping.css not found at ${tokenMappingPath}. Run 'pnpm build-mapping' first.`)
  }
  const tokenMappingCss = fs.readFileSync(tokenMappingPath, 'utf8')

  // Read inject.js from skin-core (shared across all targets)
  const injectJsPath = path.resolve(__dirname, '../../../skin-core/src/inject.js')
  if (!fs.existsSync(injectJsPath)) throw new Error(`missing ${injectJsPath}`)
  const injectJs = fs.readFileSync(injectJsPath, 'utf8')

  // Build bootstrap HTML fragment (inline CSS + data URI images + inject.js)
  const combinedCss = tokenMappingCss + '\n' + css
  const bootstrap = await buildBootstrap({ css: combinedCss, injectJs, marker: MARKER })

  // Create temp work dir
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'opencode-skin-patch-'))
  log(`patch work dir: ${workDir}`)

  const outDir = path.join(workDir, 'out')
  const outAsar = path.join(workDir, 'out.asar')

  try {
    // Extract
    log(`extracting ${asarPath}`)
    await asar.extractAll(asarPath, outDir)

    const htmlPath = path.join(outDir, RENDERER_HTML)
    let html = fs.readFileSync(htmlPath, 'utf8')

    // Restore pristine html if --force
    if (force) {
      const pristineHtml = asar.extractFile(backupPath, RENDERER_HTML).toString('utf8')
      if (pristineHtml.includes(MARKER)) {
        throw new Error('pristine backup is NOT pristine (contains the marker)')
      }
      fs.writeFileSync(htmlPath, pristineHtml, 'utf8')
      html = pristineHtml
      log(`restored pristine index.html from ${backupPath}`)
    }

    // Pristine check
    if (html.includes(MARKER) && !force) {
      throw new Error('index.html already patched; use force:true to re-patch')
    }
    if (!html.includes('oc-theme-preload-script')) {
      throw new Error('unexpected index.html content; refusing to patch')
    }

    // Inject bootstrap before </head>
    const patchedHtml = injectBootstrapIntoHtml(html, bootstrap)
    log(`bootstrap length: ${bootstrap.length}`)
    fs.writeFileSync(htmlPath, patchedHtml, 'utf8')

    // Replace favicon
    const faviconSrc = path.join(SRC, 'icon.svg')
    if (fs.existsSync(faviconSrc)) {
      const faviconTarget = path.join(outDir, FAVICON_ENTRY)
      const favicon = fs.readFileSync(faviconSrc, 'utf8')
      if (!favicon.includes('<svg')) throw new Error('favicon svg invalid')
      fs.writeFileSync(faviconTarget, favicon, 'utf8')
      log('replaced favicon-v3.svg')
    }

    // Repack
    log(`repacking to ${outAsar}`)
    await asar.createPackage(outDir, outAsar)

    // Verify
    const check = asar.extractFile(outAsar, RENDERER_HTML).toString('utf8')
    if (!check.includes(MARKER)) {
      throw new Error('repacked asar missing marker')
    }
    const size = fs.statSync(outAsar).size
    log(`verified marker in repacked asar (${size} bytes)`)

    // Install
    fs.unlinkSync(asarPath)
    fs.renameSync(outAsar, asarPath)
    log(`installed patched app.asar (${size} bytes)`)

    console.log('')
    console.log('✓ Patch complete!')

    if (autoRestart && !allowRunning) {
      launchOpenCode(appDir)
      console.log('✓ OpenCode restarted with the custom skin active.')
    } else {
      console.log('Done. Launch OpenCode manually to see the custom skin.')
    }

    console.log('')
    console.log('To revert: close the app, then run:')
    console.log(`  cp '${backupPath}' '${asarPath}'`)
  } finally {
    try {
      fs.rmSync(workDir, { recursive: true, force: true })
    } catch {}
  }
}
