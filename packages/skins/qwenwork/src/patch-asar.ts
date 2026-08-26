// src/patch-asar.ts
// Cross-platform asar binary patcher for QwenWork Desktop.
//
// Strategy: BINARY PATCH the asar file directly.
// Only modifies the index.html content in the data section and updates
// the corresponding header entries (size + offsets). The header JSON is
// padded with trailing spaces to maintain EXACTLY the same byte length,
// so ALL other file entries (including unpacked markers for sharp, node-pty,
// etc.) remain untouched and at their original offsets.
//
// asar binary format:
//   [4 bytes: uint32LE = 4]        (pickle1 size)
//   [4 bytes: uint32LE]            (data_size = headerStringSize + 8)
//   [4 bytes: uint32LE = 4]        (pickle2 size)
//   [4 bytes: uint32LE]            (headerStringSize)
//   [headerStringSize bytes: JSON] (header)
//   [data section: file contents]
//
// File offsets in the header are relative to the start of the data section.
import fs from 'node:fs'
import path from 'node:path'
import os from 'node:os'
import crypto from 'node:crypto'
import { fileURLToPath } from 'node:url'
import { execSync, spawn } from 'node:child_process'
import { buildBootstrap } from '@skins/core/bootstrap-builder'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const SRC = __dirname

const MARKER = 'qwenwork-skin'
const RENDERER_HTML = 'out/renderer/index.html'

const DEFAULT_APP_DIRS: Record<string, string> = {
  win32: path.join(os.homedir(), 'AppData', 'Local', 'Programs', 'QwenWorkCN'),
  darwin: '/Applications/QwenWorkCN.app',
  linux: '/opt/QwenWorkCN',
}

const APP_EXECUTABLES: Record<string, string> = {
  win32: 'QwenWorkCN.exe',
  darwin: 'Contents/MacOS/QwenWorkCN',
  linux: 'qwenworkcn',
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

function isAppRunning(): boolean {
  try {
    const platform = os.platform()
    if (platform === 'win32') {
      const result = execSync('tasklist /FI "IMAGENAME eq QwenWorkCN.exe"', { encoding: 'utf8' })
      return result.includes('QwenWorkCN.exe')
    }
    try {
      execSync('pgrep -f QwenWorkCN', { encoding: 'utf8', stdio: 'pipe' })
      return true
    } catch {
      return false
    }
  } catch {
    return false
  }
}

function killApp(): void {
  log('Killing QwenWorkCN processes...')
  try {
    const platform = os.platform()
    if (platform === 'win32') {
      execSync('taskkill /F /IM QwenWorkCN.exe', { stdio: 'pipe' })
    } else {
      execSync('pkill -f QwenWorkCN', { stdio: 'pipe' })
    }
    const start = Date.now()
    while (isAppRunning() && Date.now() - start < 5000) {
      // 100ms synchronous sleep — cross-platform (no shell dependency)
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100)
    }
    log('QwenWorkCN processes terminated')
  } catch {
    log('No QwenWorkCN processes to kill (or kill failed)')
  }
}

function launchApp(appDir: string): void {
  const platform = os.platform()
  const exeRelative = APP_EXECUTABLES[platform]
  if (!exeRelative) {
    fail(`unsupported platform: ${platform}`)
  }

  const exePath = path.join(appDir, exeRelative)
  log(`Launching QwenWorkCN from ${exePath}`)

  try {
    if (platform === 'win32') {
      spawn(exePath, [], { detached: true, stdio: 'ignore' }).unref()
    } else if (platform === 'darwin') {
      spawn('open', ['-a', appDir], { detached: true, stdio: 'ignore' }).unref()
    } else {
      spawn(exePath, [], { detached: true, stdio: 'ignore' }).unref()
    }
    log('QwenWorkCN launched')
  } catch (err) {
    log(`Warning: Failed to launch QwenWorkCN: ${err}`)
  }
}

// ─── asar binary format helpers ──────────────────────────────────────────
//
// asar 文件的二进制格式（@electron/asar 的 Chromium Pickle 序列化）：
//
//   [8 bytes: sizePickle]
//     [0..3]   uint32LE = 4 (payload size，固定)
//     [4..7]   uint32LE = headerPickle 总字节数
//   [headerSize bytes: headerPickle]
//     [0..3]   uint32LE = payload size (= 4 + paddedStrLen)
//     [4..7]   uint32LE = JSON 字符串字节数 (strLen)
//     [8..]    JSON 字符串 (strLen 字节)
//     [padding] 对齐到 4 字节 (strLen % 4 != 0 时补齐)
//   [data section: 文件内容，从 offset 8 + headerSize 开始]

interface AsarHeader {
  json: any
  headerString: string
  headerStringSize: number
  dataOffset: number
}

function readAsarHeader(asarPath: string): AsarHeader {
  const fd = fs.openSync(asarPath, 'r')
  try {
    // 读取 sizePickle (8 字节)
    const sizeBuf = Buffer.alloc(8)
    fs.readSync(fd, sizeBuf, 0, 8, 0)

    const sizePicklePayload = sizeBuf.readUInt32LE(0) // 固定 4
    const headerSize = sizeBuf.readUInt32LE(4) // headerPickle 总大小

    if (sizePicklePayload !== 4) {
      throw new Error(`unexpected asar header format (size pickle payload: ${sizePicklePayload})`)
    }

    // 读取 headerPickle（从 offset 8 开始，共 headerSize 字节）
    const headerBuf = Buffer.alloc(headerSize)
    fs.readSync(fd, headerBuf, 0, headerSize, 8)

    // headerPickle 内：offset 4 是 JSON 字符串长度，offset 8 起是 JSON
    const headerStringSize = headerBuf.readUInt32LE(4)
    const headerString = headerBuf.toString('utf8', 8, 8 + headerStringSize)
    const json = JSON.parse(headerString)

    // 数据从 offset 8 + headerSize 开始
    const dataOffset = 8 + headerSize

    return { json, headerString, headerStringSize, dataOffset }
  } finally {
    fs.closeSync(fd)
  }
}

// Navigate to a file entry in the asar header tree.
function getFileEntry(header: any, filePath: string): any {
  const parts = filePath.split('/').filter(Boolean)
  let current = header
  for (const part of parts) {
    if (!current.files || !current.files[part]) return null
    current = current.files[part]
  }
  return current
}

// 对齐到 4 字节
function align4(n: number): number {
  return (n + 3) & ~3
}

// 用 Chromium Pickle 格式构建 asar header buffer。
// 返回 [sizePickle(8B) + headerPickle] 的完整拼接。
function buildAsarHeaderBuf(headerJson: any): Buffer {
  const jsonStr = JSON.stringify(headerJson)
  const strLen = Buffer.byteLength(jsonStr, 'utf8')
  const paddedStrLen = align4(strLen)

  // headerPickle: [payload_size][str_len][json][padding]
  const headerPickle = Buffer.alloc(4 + 4 + paddedStrLen)
  headerPickle.writeUInt32LE(4 + paddedStrLen, 0) // payload size
  headerPickle.writeUInt32LE(strLen, 4) // 字符串长度
  headerPickle.write(jsonStr, 8, strLen, 'utf8') // JSON 内容
  // 剩余 padding 默认 0

  // sizePickle: [4][headerPickle.length]
  const sizePickle = Buffer.alloc(8)
  sizePickle.writeUInt32LE(4, 0) // payload size（一个 uint32）
  sizePickle.writeUInt32LE(headerPickle.length, 4)

  return Buffer.concat([sizePickle, headerPickle])
}

/**
 * Resolve the actual app.asar location on Windows.
 * QwenWorkCN may install to `C:\Program Files\QwenWorkCN\<version>\resources\app.asar`
 * (multi-version layout managed by Launcher/Updater) instead of the default AppData path.
 * Reads `updater.cfg` from the install root to find the current version directory.
 */
function resolveWindowsAppDir(): string | undefined {
  const programFilesDir = 'C:\\Program Files\\QwenWorkCN'
  const updaterCfg = path.join(programFilesDir, 'updater.cfg')
  if (fs.existsSync(updaterCfg)) {
    try {
      const cfg = JSON.parse(fs.readFileSync(updaterCfg, 'utf8'))
      if (cfg.currentVersion) {
        const versionDir = path.join(programFilesDir, cfg.currentVersion)
        if (fs.existsSync(path.join(versionDir, 'resources', 'app.asar'))) {
          log(`Found QwenWorkCN via updater.cfg (version ${cfg.currentVersion})`)
          return versionDir
        }
      }
    } catch {
      // fall through
    }
    // fallback: pick the latest version directory by name
    try {
      const entries = fs.readdirSync(programFilesDir)
      const versions = entries
        .filter((e) => /^\d+\.\d+/.test(e) && fs.statSync(path.join(programFilesDir, e)).isDirectory())
        .sort()
        .reverse()
      for (const v of versions) {
        const dir = path.join(programFilesDir, v)
        if (fs.existsSync(path.join(dir, 'resources', 'app.asar'))) {
          log(`Found QwenWorkCN at ${dir}`)
          return dir
        }
      }
    } catch {
      // fall through
    }
  }
  return undefined
}

function resolveAppDir(platform: string): string {
  // 1. Windows: check Program Files multi-version layout first
  if (platform === 'win32') {
    const winDir = resolveWindowsAppDir()
    if (winDir) return winDir
  }
  // 2. Default path from table
  const defaultDir = DEFAULT_APP_DIRS[platform]
  if (!defaultDir) fail(`unsupported platform: ${platform}`)
  return defaultDir
}

export async function patchAsar(options: PatchOptions = {}): Promise<void> {
  const platform = os.platform()

  const appDir = options.appDir || resolveAppDir(platform)
  const resourcesDir =
    platform === 'darwin'
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
    fail('app.asar already patched - use --force to re-patch, or restore the .bak')
  }

  if (isAppRunning()) {
    if (allowRunning) {
      log('QwenWorkCN is running, but allowRunning is set - proceeding anyway')
    } else {
      killApp()
    }
  }

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

  // ─── Build bootstrap content ───────────────────────────────────────────
  const cssPath = path.join(SRC, 'skin.css')
  if (!fs.existsSync(cssPath)) {
    throw new Error(`skin.css not found in src/`)
  }
  const css = fs.readFileSync(cssPath, 'utf8')
  log(`CSS loaded (${css.length} bytes)`)

  // Read token-mapping CSS (generated by apply step)
  const tokenMappingPath = path.join(SRC, '../dist/token-mapping.css')
  if (!fs.existsSync(tokenMappingPath)) {
    throw new Error(`token-mapping.css not found at ${tokenMappingPath}. Run 'pnpm apply' first.`)
  }
  const tokenMappingCss = fs.readFileSync(tokenMappingPath, 'utf8')
  log(`token-mapping.css loaded (${tokenMappingCss.length} bytes)`)

  const injectJsPath = path.resolve(__dirname, '../../../skin-core/src/inject.js')
  if (!fs.existsSync(injectJsPath)) throw new Error(`missing ${injectJsPath}`)
  const injectJs = fs.readFileSync(injectJsPath, 'utf8')

  const combinedCss = tokenMappingCss + '\n' + css
  const bootstrap = await buildBootstrap({ css: combinedCss, injectJs, marker: MARKER })

  // ─── Binary patch the asar ─────────────────────────────────────────────
  // Always read from the pristine backup when --force, to avoid compounding patches.
  const sourceAsar = force && fs.existsSync(backupPath) ? backupPath : asarPath
  log(`binary patching asar (source: ${sourceAsar})`)

  // Parse header
  const { json: header, headerStringSize, dataOffset } = readAsarHeader(sourceAsar)
  log(`parsed asar header: ${headerStringSize} bytes JSON, data at offset ${dataOffset}`)

  // Count unpacked entries for logging (these will be preserved by binary patching)
  let unpackedCount = 0
  function countUnpacked(node: any): void {
    if (node.unpacked) unpackedCount++
    if (node.files) Object.values(node.files).forEach(countUnpacked)
  }
  countUnpacked(header)
  log(`found ${unpackedCount} unpacked entries (will be preserved by binary patch)`)

  // Find the HTML file entry
  const htmlEntry = getFileEntry(header, RENDERER_HTML)
  if (!htmlEntry || htmlEntry.offset === undefined) {
    throw new Error(`${RENDERER_HTML} not found in asar header`)
  }

  // offset may be a string in some asar implementations — coerce to number
  const originalHtmlOffset = Number(htmlEntry.offset)
  const originalHtmlSize = Number(htmlEntry.size)

  if (isNaN(originalHtmlOffset) || isNaN(originalHtmlSize)) {
    throw new Error(`invalid HTML entry: offset=${htmlEntry.offset}, size=${htmlEntry.size}`)
  }

  // Read the HTML content from the data section
  const sourceFd = fs.openSync(sourceAsar, 'r')
  const htmlBuf = Buffer.alloc(originalHtmlSize)
  fs.readSync(sourceFd, htmlBuf, 0, originalHtmlSize, dataOffset + originalHtmlOffset)

  // Read the entire file for later use
  const totalFileSize = fs.statSync(sourceAsar).size
  const dataSectionSize = totalFileSize - dataOffset
  fs.closeSync(sourceFd)

  let html = htmlBuf.toString('utf8')

  if (html.includes(MARKER) && !force) {
    throw new Error('index.html already patched; use --force to re-patch')
  }
  if (!html.includes('<head>') || !html.includes('</head>')) {
    throw new Error('unexpected index.html content; refusing to patch')
  }

  // Strip any stale bootstrap blocks from previous patch generations.
  // Earlier versions used the marker 'qwenwork-maid-atelier'; if a .bak was
  // taken from an already-patched asar, those old blocks persist forever and
  // their CSS variables fight with the current skin. Remove them before
  // injecting, so --force always produces exactly one bootstrap.
  const staleBlock = /<!-- qwenwork-maid-atelier start -->[\s\S]*?<!-- qwenwork-maid-atelier end -->/g
  if (staleBlock.test(html)) {
    html = html.replace(staleBlock, '')
    log('stripped stale qwenwork-maid-atelier bootstrap block(s)')
  }

  // Inject bootstrap immediately before </head>
  const injected = html.replace('</head>', bootstrap + '</head>')
  const injectedBuf = Buffer.from(injected, 'utf8')
  const sizeDelta = injectedBuf.length - originalHtmlSize

  log(`injected bootstrap (${bootstrap.length} bytes) into index.html`)
  log(`HTML size: ${originalHtmlSize} → ${injectedBuf.length} (delta: +${sizeDelta})`)

  // Update header: only the HTML file's size and offsets of subsequent files
  htmlEntry.size = injectedBuf.length

  // Update integrity hash for the modified file
  const newHash = crypto.createHash('sha256').update(injectedBuf).digest('hex')
  if (htmlEntry.integrity && htmlEntry.integrity.hash) {
    htmlEntry.integrity.hash = newHash
    if (htmlEntry.integrity.blocks && htmlEntry.integrity.blocks.length > 0) {
      htmlEntry.integrity.blocks[0] = newHash
    }
    log(`updated integrity hash: ${newHash}`)
  }

  const htmlEndOffset = originalHtmlOffset + originalHtmlSize
  function shiftOffsets(node: any): void {
    if (node.files) {
      for (const child of Object.values(node.files)) {
        shiftOffsets(child)
      }
    } else if (node.offset !== undefined && node !== htmlEntry && Number(node.offset) >= htmlEndOffset) {
      node.offset = String(Number(node.offset) + sizeDelta)
    }
  }
  shiftOffsets(header)

  // ─── Write the patched asar ─────────────────────────────────────────────
  // asar 结构: [8B sizePickle][headerPickle][data]
  // offset 字段是相对于 data section 起始位置的，header 大小变化不影响相对 offset。
  const newHeaderBuf = buildAsarHeaderBuf(header)
  const newDataOffset = newHeaderBuf.length // = 8 + newHeaderSize

  log(`header: ${newHeaderBuf.length} bytes, data offset: ${newDataOffset}`)

  const workAsar = path.join(os.tmpdir(), `qwenwork-skin-patch-${Date.now()}.asar`)
  try {
    const outFd = fs.openSync(workAsar, 'w')
    const sourceFd2 = fs.openSync(sourceAsar, 'r')

    // 1. 写新 header（pickle 格式）
    fs.writeSync(outFd, newHeaderBuf, 0, newHeaderBuf.length, 0)

    // 2. 读原始 data section
    const afterBuf = Buffer.alloc(dataSectionSize)
    fs.readSync(sourceFd2, afterBuf, 0, dataSectionSize, dataOffset)

    // 3. 禁用 OOM watchdog（等长替换，不影响 offset）
    const watchdogOriginal = Buffer.from('oomWatchdogService.start()')
    const watchdogReplacement = Buffer.from('0/*disabled-watchdog-pad*/')
    if (watchdogOriginal.length !== watchdogReplacement.length) {
      throw new Error('watchdog replacement length mismatch')
    }
    let watchdogPatched = false
    for (let i = 0; i <= afterBuf.length - watchdogOriginal.length; i++) {
      if (afterBuf.subarray(i, i + watchdogOriginal.length).equals(watchdogOriginal)) {
        watchdogReplacement.copy(afterBuf, i)
        watchdogPatched = true
        log('disabled OOM watchdog in main.js (equal-length replacement)')
        break
      }
    }
    if (!watchdogPatched) {
      log('warning: OOM watchdog string not found in data section (may already be disabled)')
    }

    // 4. 分割 data section：HTML 之前 / HTML 之后
    const beforeHtml = afterBuf.slice(0, originalHtmlOffset)
    const afterHtml = afterBuf.slice(originalHtmlOffset + originalHtmlSize)

    // 5. 写数据：beforeHtml + 新HTML + afterHtml
    fs.writeSync(outFd, beforeHtml, 0, beforeHtml.length, newDataOffset)
    fs.writeSync(outFd, injectedBuf, 0, injectedBuf.length, newDataOffset + beforeHtml.length)
    fs.writeSync(outFd, afterHtml, 0, afterHtml.length, newDataOffset + beforeHtml.length + injectedBuf.length)

    fs.closeSync(sourceFd2)
    fs.closeSync(outFd)

    // ─── Verify ──────────────────────────────────────────────────────────
    const { json: checkHeader, dataOffset: checkDataOffset } = readAsarHeader(workAsar)
    const checkEntry = getFileEntry(checkHeader, RENDERER_HTML)
    if (!checkEntry) throw new Error('patched asar missing HTML entry')

    const checkFd = fs.openSync(workAsar, 'r')
    const checkHtmlBuf = Buffer.alloc(Number(checkEntry.size))
    fs.readSync(checkFd, checkHtmlBuf, 0, Number(checkEntry.size), checkDataOffset + Number(checkEntry.offset))
    fs.closeSync(checkFd)

    const checkHtml = checkHtmlBuf.toString('utf8')
    if (!checkHtml.includes(MARKER)) {
      throw new Error('patched asar missing marker in HTML')
    }

    // Verify unpacked entries are preserved
    let checkUnpackedCount = 0
    function countCheckUnpacked(node: any): void {
      if (node.unpacked) checkUnpackedCount++
      if (node.files) Object.values(node.files).forEach(countCheckUnpacked)
    }
    countCheckUnpacked(checkHeader)

    if (checkUnpackedCount !== unpackedCount) {
      throw new Error(`unpacked count mismatch: ${checkUnpackedCount} vs ${unpackedCount}`)
    }

    const patchedSize = fs.statSync(workAsar).size
    log(`verified: marker present, ${checkUnpackedCount} unpacked entries preserved`)
    log(`patched asar: ${patchedSize} bytes (original: ${totalFileSize})`)

    // Install
    fs.unlinkSync(asarPath)
    fs.renameSync(workAsar, asarPath)
    log(`installed patched app.asar`)

  } catch (err) {
    try { fs.unlinkSync(workAsar) } catch {}
    throw err
  }

  // Re-sign the app with ad-hoc signature after patching (macOS only)
  if (os.platform() === 'darwin') {
    log('re-signing app with ad-hoc signature...')
    try {
      execSync(`codesign --force --deep --sign - "${appDir}"`, { stdio: 'pipe' })
      log('app re-signed successfully')
    } catch (err) {
      log(`warning: failed to re-sign app (you may need to sign manually): ${err}`)
    }
  }

  console.log('')
  console.log('✓ Patch complete! (binary patch — all unpacked files preserved)')

  if (autoRestart && !allowRunning) {
    launchApp(appDir)
    console.log('✓ QwenWorkCN restarted with the custom skin active.')
  } else {
    console.log('Done. Launch QwenWorkCN manually to see the custom skin.')
  }

  console.log('')
  console.log('To revert: close the app, then run:')
  if (os.platform() === 'win32') {
    console.log(`  copy "${backupPath}" "${asarPath}"`)
  } else {
    console.log(`  cp '${backupPath}' '${asarPath}'`)
  }
}

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))
) {
  patchAsar({ force: true, autoRestart: true }).catch((err) => {
    console.error(err)
    process.exit(1)
  })
}
