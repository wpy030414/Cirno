# Spec — codex config 补丁引擎（patch-config.ts）

**对应模块**：`packages/skins/codex/src/patch-config.ts` + `src/index.ts` 的 config 分支

## 要实现什么

一个行级 TOML 段编辑器，把皮肤 chromeTheme 颜色写入 `~/.codex/config.toml` 的官方外观段（辅通道）。

## 行为应该是什么

`pnpm apply:codex` 时：

1. marker（`# codex-skin-marker`）幂等检测：已打且非 --force → 报错
2. 备份 `config.toml` → `config.toml.skin.bak`（不存在时必建；--no-backup 仍保留 pristine snapshot，D-010）
3. 只改四个受管段：`[desktop.appearanceLightChromeTheme]` / `[desktop.appearanceDarkChromeTheme]` 及各自 `.semanticColors` 子段——替换/补充颜色键；段内其他键（contrast/opaqueWindows/fonts）与**段外所有内容字节零改动**（CRLF/BOM 原样保留）
4. `--pin light|dark`（仅测试）：写/覆盖 `[desktop]` 段 `appearanceTheme`；还原时随 .bak 覆盖消失
5. `--force`：从 .bak（pristine）还原再打；备份含 marker（污染）→ 拒绝
6. `--restore`：.bak 覆盖回 config.toml（完全还原，与仓库「.bak 覆盖回 app.asar」同一语义）

## 边界条件

1. 首次 patch：.bak 创建；二次无 --force：报错 already patched
2. --force 且 .bak 缺失：报错；.bak 污染：报错
3. 段不存在：创建（置于文件尾）；受管键不存在：补充
4. config.toml 缺失：报错，不创建
5. 还原后与备份逐字节相等（测试断言）

## 验收标准

- [x] 单测：首次/幂等/--force/污染防护/--no-backup/--pin/还原精确/CRLF+LF 保留/纯函数性（19 项测试套件内）
- [x] 实机：用户真实 config.toml 打补丁后 projects/plugins 等段无损，还原逐字节相等
