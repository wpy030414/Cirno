// src/darwin-contract.test.ts
// darwin 平台合约测试（跨平台可跑）：注入 runner 模拟进程调用，
// 断言 find/kill/launch 的命令构造与决策逻辑——Windows CI 运行本文件
// 即可侧面守护 macOS 分支（实机行为另见 darwin-live.test.ts）。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import {
  DARWIN_APP_NAME,
  buildDarwinLaunchArgs,
  darwinKillCodex,
  darwinLaunchCodex,
  findDarwinCodexApp,
  type SyncRunner,
} from './cdp-inject.js'

function makeFakeApp(executable: boolean): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-skin-test-'))
  const appPath = path.join(dir, DARWIN_APP_NAME)
  fs.mkdirSync(path.join(appPath, 'Contents', 'MacOS'), { recursive: true })
  if (executable) fs.writeFileSync(path.join(appPath, 'Contents', 'MacOS', 'ChatGPT'), '')
  return dir
}

const ok = (stdout: string): { code: number; stdout: string; stderr: string } => ({ code: 0, stdout, stderr: '' })
const missing = (): { code: number; stdout: string; stderr: string } => ({ code: 1, stdout: '', stderr: '' })

/** 记录型 runner：按队列出队响应（队列耗尽返回 code=1） */
function recordingRunner(responses: Array<{ code: number; stdout: string; stderr: string }>): { runner: SyncRunner; calls: Array<{ cmd: string; args: string[] }> } {
  const calls: Array<{ cmd: string; args: string[] }> = []
  const runner: SyncRunner = (cmd, args) => {
    calls.push({ cmd, args })
    return responses.length > 0 ? responses.shift()! : missing()
  }
  return { runner, calls }
}

test('findDarwinCodexApp：定位 ChatGPT.app（runner 注入读 bundle id，跨平台可测）', () => {
  const dir = makeFakeApp(true)
  const { runner } = recordingRunner([ok('com.openai.codex\n')])
  const found = findDarwinCodexApp([dir], runner)
  assert.ok(found, '应定位到 ChatGPT.app')
  assert.equal(found!.appPath, path.join(dir, DARWIN_APP_NAME))
  assert.equal(found!.executablePath, path.join(dir, DARWIN_APP_NAME, 'Contents', 'MacOS', 'ChatGPT'))
  assert.equal(found!.bundleId, 'com.openai.codex')
  fs.rmSync(dir, { recursive: true, force: true })
})

test('findDarwinCodexApp：缺可执行文件 → null；空目录列表 → null', () => {
  const empty = makeFakeApp(false)
  const { runner } = recordingRunner([])
  assert.equal(findDarwinCodexApp([empty], runner), null)
  assert.equal(findDarwinCodexApp([], runner), null)
  fs.rmSync(empty, { recursive: true, force: true })
})

test('findDarwinCodexApp：bundle id 不符的同名应用被跳过，继续找下一目录', () => {
  const wrong = makeFakeApp(true)
  const right = makeFakeApp(true)
  const { runner } = recordingRunner([ok('com.example.other\n'), ok('com.openai.chat\n')])
  const found = findDarwinCodexApp([wrong, right], runner)
  assert.ok(found, '应跳过 bundle id 不符的应用')
  assert.equal(found!.appPath, path.join(right, DARWIN_APP_NAME))
  assert.equal(found!.bundleId, 'com.openai.chat')
  fs.rmSync(wrong, { recursive: true, force: true })
  fs.rmSync(right, { recursive: true, force: true })
})

test('findDarwinCodexApp：全部候选 bundle id 不符 → null', () => {
  const a = makeFakeApp(true)
  const b = makeFakeApp(true)
  const { runner } = recordingRunner([ok('com.example.other\n'), ok('com.example.also-other\n')])
  assert.equal(findDarwinCodexApp([a, b], runner), null)
  fs.rmSync(a, { recursive: true, force: true })
  fs.rmSync(b, { recursive: true, force: true })
})

test('findDarwinCodexApp：PlistBuddy 不可用/失败时宽容返回（bundleId null）', () => {
  const dir = makeFakeApp(true)
  const { runner } = recordingRunner([missing()])
  const found = findDarwinCodexApp([dir], runner)
  assert.ok(found, 'id 校验失败不应阻止定位')
  assert.equal(found!.bundleId, null)
  fs.rmSync(dir, { recursive: true, force: true })
  // runner 抛异常同样宽容
  const dir2 = makeFakeApp(true)
  const throwing: SyncRunner = () => { throw new Error('spawn ENOENT') }
  const found2 = findDarwinCodexApp([dir2], throwing)
  assert.ok(found2)
  assert.equal(found2!.bundleId, null)
  fs.rmSync(dir2, { recursive: true, force: true })
})

test('buildDarwinLaunchArgs：open -a <app> --args 参数组装', () => {
  assert.deepEqual(buildDarwinLaunchArgs('/Applications/ChatGPT.app', ['--remote-debugging-port=9335']), [
    '-a',
    '/Applications/ChatGPT.app',
    '--args',
    '--remote-debugging-port=9335',
  ])
  assert.deepEqual(buildDarwinLaunchArgs('/Applications/ChatGPT.app', []), ['-a', '/Applications/ChatGPT.app', '--args'])
})

test('darwinKillCodex：已退出场景（pgrep code≠0）→ 无 sleep，命令序列正确', () => {
  // 响应按调用顺序出队：第一次 pkill 也消耗一个响应
  const { runner, calls } = recordingRunner([ok(''), missing()])
  darwinKillCodex(runner, 100)
  assert.deepEqual(calls, [
    { cmd: 'pkill', args: ['-x', 'ChatGPT'] },
    { cmd: 'pgrep', args: ['-x', 'ChatGPT'] },
    { cmd: 'pkill', args: ['-9', '-x', 'ChatGPT'] },
    { cmd: 'pkill', args: ['-9', '-f', '[C]hatGPT.app/Contents/Resources/codex'] },
  ])
})

test('darwinKillCodex：优雅退出中（pgrep 先 0 后 1）→ 含 sleep 轮询且循环有界', () => {
  const { runner, calls } = recordingRunner([ok(''), ok('42\n'), missing()])
  darwinKillCodex(runner, 5000)
  assert.deepEqual(calls.slice(0, 3), [
    { cmd: 'pkill', args: ['-x', 'ChatGPT'] },
    { cmd: 'pgrep', args: ['-x', 'ChatGPT'] },
    { cmd: 'sleep', args: ['0.2'] },
  ])
  assert.ok(calls.some((c) => c.cmd === 'pgrep'), '应轮询 pgrep')
  assert.ok(calls.some((c) => c.cmd === 'sleep'), '存活期间应 sleep 轮询')
  assert.equal(calls.filter((c) => c.cmd === 'pgrep').length, 2, 'pgrep 只轮询到退出为止')
  const last = calls.slice(-2)
  assert.deepEqual(last, [
    { cmd: 'pkill', args: ['-9', '-x', 'ChatGPT'] },
    { cmd: 'pkill', args: ['-9', '-f', '[C]hatGPT.app/Contents/Resources/codex'] },
  ])
})

test('darwinLaunchCodex：open 命令构造；code≠0 抛错；appPath 缺失抛错', () => {
  const { runner, calls } = recordingRunner([ok('')])
  const pid = darwinLaunchCodex({ appPath: '/Applications/ChatGPT.app' }, ['--remote-debugging-port=9335'], runner)
  assert.equal(pid, 0, 'open 不返回 pid，launch 返回 0')
  assert.deepEqual(calls, [
    { cmd: 'open', args: ['-a', '/Applications/ChatGPT.app', '--args', '--remote-debugging-port=9335'] },
  ])
  const failing = recordingRunner([{ code: 2, stdout: '', stderr: 'app not found' }])
  assert.throws(() => darwinLaunchCodex({ appPath: '/Applications/ChatGPT.app' }, [], failing.runner), /failed to launch Codex: app not found/)
  assert.throws(() => darwinLaunchCodex({}, [], recordingRunner([]).runner), /darwin launch requires appPath/)
})
