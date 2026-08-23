# Personalized Agent — Universal Skin Pipeline

**通用皮肤引擎 monorepo**：将任意原始图片素材 → 按规则取色 → 生成设计 tokens → 应用到任何 GUI Agent。

## 这个项目是什么？

一套把「图片素材 → 可换肤的桌面端 GUI Agent」打通的全自动管线：

- **skin-core**：从图片提取颜色（k-means++ 聚类），生成 **46 个设计 tokens**（light / dark 两套完整方案）
- **skin-assets**：共享图片库，容纳**多个主题**（`<name>.theme/` 目录），每个主题以 `manifest.json` 统一角色映射 + 取色声明
- **skins/<target>**：每个目标 app 一个皮肤包，只写 token-mapping + 组件 CSS + 打补丁引擎

## 为什么存在？

- **每个 GUI Agent 都要手写皮肤**：重复劳动，无法复用
- **取色靠人工**：设计师手动挑色，无法自动化
- **token 命名空间不统一**：每个 app 一套命名，皮肤移植成本高

把「取色」与「适配」解耦后，换新 target 只需写一份 token-mapping 和 CSS。

## 如何安装和运行？

前置：Node.js 20+、pnpm。

```bash
pnpm install

# 1. 从素材取色生成 tokens（选中主题，未指定则终端交互选择）
pnpm build-tokens --theme maid-atelier

# 2. 预览取色结果
pnpm preview

# 3. 生成各 skin 的 token 映射 CSS
pnpm build-mapping:qwenwork

# 4. 给 QwenWork 打皮肤补丁（自动备份 + 重启，跟随 build-tokens 选定的主题）
pnpm apply:qwenwork
```

还原皮肤：把 `app.asar.skin.bak` 覆盖回 `app.asar` 即可（apply 完成后控制台会打印命令）。

### 命令集

| 命令 | 说明 |
|---|---|
| `pnpm build-tokens` | **主题唯一选择点**：从选中主题的 colorSource 图提取颜色，生成 light+dark 双套 tokens.json（未指定主题则终端交互选择） |
| `pnpm preview` | 生成 palette.html 可视化预览（簇 + 双套 token，跟随活动主题） |
| `pnpm dev:qwenwork` | 关闭 QwenWorkCN，带 `--enable-devtools` 重启 |
| `pnpm build-mapping:qwenwork` | 读取 tokens，生成 `dist/token-mapping.css` |
| `pnpm apply:qwenwork` | 给 QwenWorkCN Desktop 打补丁（二进制补丁策略，跟随活动主题） |
| `pnpm dev:opencode` | 关闭 OpenCode，带 `--enable-devtools` 重启 |
| `pnpm build-mapping:opencode` | 读取 tokens，生成 `dist/token-mapping.css` |
| `pnpm apply:opencode` | 给 OpenCode Desktop 打补丁（extract/repack 策略，跟随活动主题） |
| `pnpm dev:codex` | 关闭 Codex Desktop，带 CDP 调试端口重启并注入皮肤（等价 apply:codex） |
| `pnpm build-mapping:codex` | 读取 tokens，生成 token-mapping.css（CDP 层）+ token-mapping.toml（官方 chrome 层）+ 官方导入串 |
| `pnpm apply:codex` | 给 Codex Desktop 换肤（官方 config 通道 + CDP 运行时注入，跟随活动主题；`--pin light\|dark` 仅测试，`--restore` 还原） |

主题选择：**只在 build-tokens**——`--theme <name>` 参数 > `SKIN_THEME` 环境变量 > 终端交互选择；选定主题写入 `dist/tokens.json`，preview / apply 自动跟随。可用主题 = `packages/skin-assets/` 下 `<name>.theme/` 目录。切换主题 = `pnpm build-tokens --theme <name>` 后重跑 apply。

apply 支持参数（透传到各 skin 包）：

| 参数 | 默认 | 含义 |
|---|---|---|
| `--no-force` | force=true | 关闭强制重 patch（已打过补丁时报错） |
| `--no-backup` | false | 跳过备份（仍保留 pristine snapshot 供 --force 使用） |
| `--allow-running` | false | 不关闭运行中的 app |

## 当前处于什么状态？

- ✅ skin-core：k-means++ 确定性取色（mulberry32 种子 PRNG）+ 46 tokens light/dark 双套生成
- ✅ skin-assets：多主题结构（`<name>.theme/` × N，各带 manifest）+ 主题交互选择（`--theme` / `SKIN_THEME`）
- ✅ skins/qwenwork：二进制补丁引擎 + token-mapping（~200 条映射）+ 组件 CSS
- ✅ skins/opencode：extract/repack 引擎 + token-mapping（~300 条映射）+ 组件 CSS
- ✅ skins/codex：官方 chrome 配置引擎 + CDP 运行时注入（180 条映射）+ 立绘装饰层（MSIX 受保护目录不碰 asar）
- ✅ 共享注入：bootstrap 构建 + data URI 图片注入 + `data-skin` 属性保活
- ✅ 双套对比度达标：light/dark 各渠道 28 项对比度检查全绿

## 使用了哪些核心技术？

| 层 | 技术 | 说明 |
|---|---|---|
| 取色 | sharp + k-means++ | 100x100 resize、每 2 像素采样、按 L* 亮度排序 |
| Token | 固定 schema | 46 tokens（neutral 12 / brand 6 / semantic 12 / text 5 / surface 4 / border 4 / input 2 / accent 1），light+dark 双套 |
| 映射 | 各 skin 包函数映射 | token-mapping.ts 把通用 tokens 映射到目标 CSS variables（支持计算与 fallback） |
| 注入 | data: URI + MutationObserver | 图片内联进 HTML，`html[data-skin]` 属性保活 |
| 打包 | 二进制补丁 / `@electron/asar` | qwenwork 用二进制（unpacked 原生模块），opencode 用 extract/repack |
| 运行时注入 | CDP（WebSocket，仅回环） | codex 用 COM 激活传参启动 + 运行时注入（MSIX 受保护目录不碰 asar） |
| 运行时 | `tsx` (ES2022 + NodeNext) | TypeScript 直接执行，无编译步骤 |

## 项目结构

```
personalized-agent/
├── package.json                # 根：路由脚本
├── pnpm-workspace.yaml         # monorepo workspace
├── README.md / AGENTS.md       # 项目总览（唯一文档入口）
├── scripts/
│   ├── dev-qwenwork.mjs        # kill + open --enable-devtools
│   └── dev-opencode.mjs
├── packages/
│   ├── skin-core/              # 通用管线（取色 + token 生成 + 共享注入）
│   │   └── src/
│   │       ├── index.ts             # 入口 buildTokens() + CLI
│   │       ├── types.ts             # TOKEN_KEYS（46 个）/ Tokens / BuildOptions
│   │       ├── extract-colors.ts    # k-means++ 取色（确定性）
│   │       ├── generate-tokens.ts   # 簇 → light/dark 双套 tokens
│   │       ├── assets-loader.ts     # manifest 加载（roles + colorSource）
│   │       ├── bootstrap-builder.ts # 共享 bootstrap HTML 片段生成
│   │       ├── inject.js            # data-skin 属性保活（共享）
│   │       └── palette-preview.ts   # HTML 预览生成
│   ├── skin-assets/            # 共享图片资产（纯静态）
│   └── skins/
│       ├── qwenwork/           # QwenWork 实现
│       │   └── src/{index,patch-asar,token-mapping}.ts + skin.css
│       └── opencode/           # OpenCode 实现
│       └── codex/              # Codex Desktop 实现
│           └── src/{index,patch-asar,token-mapping}.ts + skin.css
└── docs/
    ├── PRD.md                  # 产品需求（为什么做、做什么）
    ├── ARCHITECTURE.md         # 系统组织（三层架构、数据流）
    ├── DECISIONS.md            # 设计决策记录（历史上下文）
    └── specs/                  # 模块级实现契约
```
