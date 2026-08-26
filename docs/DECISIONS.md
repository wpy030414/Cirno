# DECISIONS

**为什么这样设计。** 记录重要技术/架构决策的历史上下文，避免未来 Agent 因不了解原因而重复推翻已有设计。

---

## D-001 — 为什么做 monorepo 而不是单项目？

**问题**：最初有两个独立仓库（qwenwork-skin-maid / opencode-deep-whale），架构几乎一模一样，但图片重复存储、代码重复。

**可选方案**：
- A. 保持两个独立仓库
- B. 合并为 monorepo + 共享图片
- C. 合并为 monorepo + 共享图片 + 共享 patch-asar 引擎
- D. 合并为 monorepo + 通用取色管线 + 各 target 只写 token-mapping

**选择**：D

**为什么**：
- A 的缺点：图片重复、代码重复、换 target 要重写取色逻辑
- B 的缺点：还是重复代码
- C 的缺点：QwenWork 用二进制补丁（unpacked 模块问题），OpenCode 用 extract/repack，两套策略难统一
- D 的优点：取色算法统一，target 只写映射，扩展性好

**影响**：
- 建了 skin-core / skin-assets / skins/<target> 三层结构
- 每个 target 包保留自己的 patch-asar 实现（不强制统一）
- 未来加新 target 只需写 token-mapping + patch-asar

---

## D-002 — token schema 的演进：动态 → 固定 10 → 固定 46

**问题**：取色结果是颜色簇，怎么映射到 CSS variables？

**演进过程**：
1. **早期**：动态 schema（根据聚类结果自动生成 token 名）——token 名不稳定，skin 包难写映射
2. **MVP**：固定 10 个 key（brand-primary / bg-base / text-base 等）——简单稳定，但语义覆盖不足（无状态色、无输入态、无边框 focus）
3. **当前**：固定 **46 个 key**（neutral 12 / brand 6 / semantic 12 / text 5 / surface 4 / border 4 / input 2 / accent 1），**light/dark 双套完整方案**

**为什么是固定 schema**：
- A（动态）的缺点：token 名不稳定，skin 包难写映射
- 固定 schema 的优点：简单、稳定、skin 包只写固定映射

**为什么是 46 + 双套**：
- 8 大类别覆盖通用 GUI 主题化需求（对比度要求的语义 slot 全部有）
- light/dark 双套让 skin 包直接按主题取用，不在 mapping 层自行推导暗色变体（推导必然出现对比度偏差）

**影响**：
- skin-core 保证输出 46 个 token，缺 key 会被 `validateTokens` 拦截
- 簇数 < 8、中性 < 2、彩色 < 2 时抛错（避免生成无意义的 tokens）

**何时重新考虑**：新 target 需要新的语义 slot 时，先审查是否真需要扩 core schema，还是用该 skin 的 COMPONENT_PALETTE 表达。

---

## D-003 — 为什么用自研 k-means 而不是现成库？

**问题**：取色算法怎么选？

**可选方案**：
- A. color-thief（现成库，快）
- B. vibrant.js（Google 出品，语义色）
- C. k-means 自己实现（可控）

**选择**：C

**为什么**：
- A 的缺点：颜色顺序不稳定，无法保证 token 语义一致
- B 的缺点：依赖重，API 复杂
- C 的优点：k-means++ 初始化稳定、**确定性 PRNG（mulberry32，种子从像素数据推导）保证可复现**、按 L* 亮度排序可控、代码透明

**影响**：
- 自己写了 `extract-colors.ts`（~220 行），100x100 resize + 每 2 像素采样
- 同一输入永远产出同一 tokens（CI / 回归可对比）

---

## D-004 — 为什么 QwenWork 用二进制补丁，OpenCode 用 extract/repack？

**问题**：两种 app 的 asar 结构不同，怎么统一？

**可选方案**：
- A. 都用 extract/repack
- B. 都用二进制补丁
- C. 各用各的（不强制统一）

**选择**：C

**为什么**：
- A 的缺点：QwenWorkCN 的 unpacked 原生模块（sharp / node-pty）在 ARM Mac 上 `extractAll()` 会因为 x64 二进制不存在而失败
- B 的缺点：OpenCode 没有 unpacked 问题，用二进制补丁反而复杂
- C 的优点：各取所长，不强求统一

**影响**：
- 每个 skin 包保留自己的 patch-asar 实现
- skin-core 不碰 patch 逻辑，只共享 bootstrap 构建
- 二进制补丁要点：header JSON 尾部空格 padding 保持等长 → 其余条目 offset 不动；OOM watchdog 等长替换；integrity hash 重算；macOS 重新签名

**何时重新考虑**：`@electron/asar` 修复 unpacked 缺失 bug，或 QwenWork 不再标记 unpacked 时，可统一为 extract/repack。

---

## D-005 — 为什么 dev 脚本用 kill + open 而不是 watch？

**问题**：调试时改 CSS 要反复 apply，怎么加速？

**可选方案**：
- A. watch 模式（chokidar 监听 → 自动 apply）
- B. kill + open（手动 apply + 重启）
- C. kill + open + devtools flag

**选择**：C（MVP 先做 B，后扩展为 C）

**为什么**：
- A 的缺点：每次 apply 2-3s，可能打断思路
- B 的缺点：要手动 kill + apply + open
- C 的优点：一键重启 + 自动开 DevTools，调试 CSS 最爽

**影响**：写了各 skin 包的 `scripts/dev.mjs`（kill + open --enable-devtools），放在子项目 package.json 里

---

## D-006 — 图片素材走 data: URI 注入，而非自定义协议或文件路径

**问题**：立绘（宫殿 + 角色）需要被 CSS 引用。可选路径：
- **A**：自定义协议（`oc://` / `qwenwork://`），注册 `protocol.registerFileProtocol`
- **B**：`url(file:///abs/path)` 绝对路径
- **C**：`url(data:image/webp;base64,...)` 内联

**选择**：**C — data: URI**

**为什么**：
- A 需要改目标 app 的 JS bundle（在 main process 注册协议），违反「只改 index.html」的最小侵入原则
- B 被 Electron 默认 CSP 拒绝（`img-src` 没放行 `file://`）
- C 直接内联到 HTML，绕开所有 CSP 限制，离线可用

**代价**：`app.asar` 体积增大约 1.3 MB（4 张 webp base64 后）；每次重 patch 都要重编码一次（可接受）

**演进插曲**：OpenCode 侧曾短暂改为「独立文件 + `oc://` 协议」方案（文件复制到 asar 内、CSS 用 `<link>` 加载），后因注入点改回 index.html（见 D-015）、且 data URI 方案完全够用，**回退到 data: URI 统一方案**。教训：不要为了「可维护性」引入额外协议依赖，除非现状真正不够用。

**何时重新考虑**：立绘数量增加到 10+ 张，体积问题凸显；或目标 app 开放了 plugin 协议注册。

---

## D-007 — 立绘作为 CSS 变量注入，而非硬编码 `url()`

**问题**：CSS 里怎么引用立绘？
- **A**：CSS 写死 `url(data:...)`
- **B**：CSS 写 `var(--background-day)`，由 JS 注入变量值

**选择**：**B — CSS 变量**

**为什么**：CSS 文件保持可读 / 可编辑（KB 级 vs MB 级）；切换立绘只需改变 JS 注入的变量值（未来多角色方便）；CSS 模板不需要字符串替换

**影响**：`buildImageInjectionScript()` 生成 4 个 CSS 变量（`--background-day` / `--background-night` / `--character-left` / `--character-right`）

---

## D-008 — Bootstrap 注入在 `</head>` 之前，而非 `</body>` 之后

**选择**：`</head>` 之前

**为什么**：让 CSS 变量在 React bundle 跑之前就位 → **避免 FOUC**（flash of unstyled content）

**代价**：HTML 解析到一半就要跑 JS——但这段 JS 只做 4 次 setProperty + 1 次 MutationObserver，微秒级

---

## D-009 — 手动解包 asar（不用 `asar.extractAll`）— QwenWork

**问题**：`@electron/asar` 的 `extractAll()` 在 ARM Mac 上报错（unpacked 的 x64 二进制不存在，`extractAll` 看到 unpacked 记录但找不到文件就抛错）

**选择**：**手动：`asar.listPackage()` + 循环 `asar.extractFile()`，跳过 ENOENT**——后因二进制补丁方案（D-004）完全不再解包

**影响**：二进制补丁天然保留所有 unpacked 条目（只改 HTML 条目 + 等长 header）

---

## D-010 — backup 文件的双重身份

**问题**：`--no-backup` 时还需不需要 `.bak`？

**选择**：**需要。`.bak` 同时承担「回滚副本」和「pristine source」两个角色**。

**为什么**：
- `--force` 重 patch 时，需要从 pristine source 还原 HTML 再打，否则会累加 bootstrap 块
- 如果用户首次 patch 用了 `--no-backup`，至少 `.bak` 还是 pristine，未来 `--force` 仍能工作
- 真正的「彻底不备份」没有提供——安全起见

**何时重新考虑**：如果用户真的不想留下任何 `.bak` 文件

---

## D-011 — 用 `html[data-skin]` 属性激活皮肤，而非独立 CSS class

**问题**：怎么让皮肤能「一键还原」？
- **A**：注入全局 class `<html class="skin">`
- **B**：注入 `<html data-skin="active">`

**选择**：**B — `data-*` 属性**

**为什么**：目标 app 的 React 根节点会频繁修改 `class`，注入 class 容易被覆盖或影响 React 的 diff；`data-*` 属性独立于 class 体系；可以携带皮肤名（`active`），未来做多皮肤时只需换值

**影响**：选择器略长：`html[data-skin][data-theme="light"]` vs `html.skin.light`

---

## D-012 — 用 MutationObserver 保活 `data-skin`，而非轮询

**问题**：目标 app 的 React 在切换主题/重渲染时会重写 `<html>` 上的属性，怎么保证 `data-skin` 不被抹掉？
- **A**：`setInterval(ensureSkinAttr, 50)` 轮询
- **B**：`MutationObserver` 监听属性变化

**选择**：**B — MutationObserver**

**为什么**：事件驱动不浪费 CPU；反应快（同步 microtask 触发）；不会和 React 渲染循环打架。旧浏览器不支持——但 Electron 是 Chromium，完全支持

**影响**：`inject.js` 监听 `attributes: true, attributeFilter: ['data-skin']`，被抹掉立刻补回。inject.js 只做属性保活，不做任何 DOM 替换（早期曾尝试替换 favicon/logo/avatar，被 React 重渲染打回且性能开销大，已简化）

---

## D-013 — 宫殿背景 opacity 0.24

**选择**：**0.24（24% 不透明）**

**为什么**：在卡片容器上 24% 足以看出宫殿轮廓，但不影响卡片内文字可读性；light 下白底 + 24% 宫殿 ≈ 柔和纹理；dark 下深蓝底 + 24% 宫殿 ≈ 微光氛围。`::before` 伪元素实现，`pointer-events: none`

**何时重新考虑**：主人觉得太淡或太浓（视觉调试后的值）

---

## D-014 — 取色只采 colorSource 角色（场景背景不进管线）

**问题**：素材里有宫殿昼/夜背景 + 角色立绘，取色应该采哪些？

**演进**：
- 早期：所有图片一起采 → 宫殿背景的蓝调主导了聚类，主题被背景色绑架
- 当前：manifest 显式声明 `colorSource: ["character-left", "character-right"]`，**管线只从角色立绘采样**

**为什么**：角色立绘是主题的「本质色」（服装/发色/肤色），背景是「氛围层」；背景主导会让中性色阶全部偏蓝，正文对比度失败。用 manifest 声明而非代码硬编码，换素材主题时不用改代码

**影响**：`getColorSourceImagePaths()` 只返回 colorSource 角色；background-day/night 仅用于运行时背景展示，不进取色

---

## D-015 — OpenCode 注入点从 oc-theme-preload.js 改为 index.html

**问题**：OpenCode 升级后原注入方案（改写 `oc-theme-preload.js`）失效，皮肤不生效

**演进**：
- 早期：改写 `out/renderer/oc-theme-preload.js`，追加 bootstrap（CSS 用 `<link>` 加载独立文件）
- 当前：**直接改 `out/renderer/index.html`**，bootstrap 内联 `<style>` + `<script>` 注入到 `</head>` 前

**为什么**：index.html 是升级后结构最稳定的注入点；内联方案与 qwenwork 完全统一（共享 `buildBootstrap` / `injectBootstrapIntoHtml`）；不再依赖 `oc://` 协议加载 CSS/图片（D-006 回退）

**影响**：改写前校验 HTML 包含 `oc-theme-preload-script` 字符串（防错文件）；favicon 替换仍保留（`icon.svg` → `favicon-v3.svg`）

---

## D-016 — token 生成产出 light/dark 两套完整方案

**问题**：皮肤要支持亮/暗双主题，映射层的暗色从哪来？

**可选方案**：
- A. 只生成一套 tokens，mapping 层自行推导暗色变体（调明度）
- B. core 生成 light/dark 两套完整方案，mapping 按主题直接取用

**选择**：**B**

**为什么**：A 的推导散落在每个 skin 包且容易出错（对比度偏差、语义色方向搞反）；B 把双主题调优集中在一处（core 内针对 light/dark 分别调对比度：brand 色阶明度范围不同、语义色 weak/base/strong 方向相反、text/surface/border 取 neutral 阶相反端），mapping 层一行 `S(key)` 取用

**影响**：`tokens.json` 结构为 `{ light, dark }`；`generateTokenPairs(clusters)` 一次产出双套

---

## D-017 — 透明度通道政策：先全禁，后仅限组件表恢复

**问题**：token 值允许带 alpha 通道吗？

**演进**：
1. **全管线禁止**（c8e2b7b）：所有 token 100% 不透明——避免半透明 token 叠加后对比度失控
2. **恢复透明度通道**（e1a6054，主人批准）：核心 token 管线仍输出 6 位不透明 hex，但**各 skin 包的 COMPONENT_PALETTE 组件表允许 8 位带 alpha hex**（如 `--bg-sidebar-material: #E5EDF9d9` 毛玻璃材质、阴影色）

**为什么**：材质类变量（毛玻璃、阴影、hover 叠层）语义上必须带透明度才能表达；而核心 token（surface/text/border 等）保持不透明保证可读性

**影响**：代码评审注意区分——核心 token 值 6 位；COMPONENT_PALETTE 可 8 位

---

## D-018 — token 映射语义分档：accent/按钮 + inverted 恒浅

**问题**：同是 brand 色，链接、按钮、强调文字对对比度的要求不同，dark 主题下更敏感（white on brand-600 只有 3.73:1）

**选择**（语义审查后）：
- **链接/强调文字**（text-accent / icon-accent / markdown-link）：light 用 `brand-600`，dark 用更亮的 `brand-500`
- **按钮背景**（control-core-button / button-primary / switch）：light 用 `brand-600`，dark 用更深的 `brand-700`（保证白字 ≥4.5:1）
- **inverted 文字**（text-inverted-* / v2-text-text-inverse / icon-inverse）：**恒定浅色**，不随主题翻转——它们永远坐在深色/强调表面上

**为什么**：一次语义审查发现旧映射「同档位」导致 dark 主题按钮对比度不达标；inverted 文字若跟随主题翻转，浅底深字场景会变成深底深字

**影响**：`brandLink` / `brandLinkHover` / `brandButton` 三个分档变量贯穿两个 skin 的语义规则

---

## D-019 — QwenWork 跟随系统模式：prefers-color-scheme 而非默认 light

**问题**：QwenWork 的「跟随系统」不设置 `data-theme` 属性。`html[data-skin]:not([data-theme])` 默认规则会强制 light 配色，OS 处于暗色模式时皮肤是「light 皮肤 + 暗色窗口」，暗色不生效

**选择**：无 `data-theme` 时用 `@media (prefers-color-scheme: light/dark)` 包裹两套语义规则

**为什么**：跟随系统模式没有 DOM 属性可挂，只能靠媒体查询判断 OS 主题

**影响**：qwenwork 的 token-mapping.css 输出 5 段（共享色阶 + light 显式 + dark 显式 + 两条媒体查询）

---

## D-020 — 背景必须不透明：去除毛玻璃方案

**问题**：早期 skin.css 用 `background: transparent` / `color-mix(... 82%, transparent)` 做侧栏/主区/输入区半透明，想让「底层背景和角色透出」

**结果**：聊天文字直接叠在装饰层上，**可读性灾难**。经主人指示撤回毛玻璃，改为**全不透明背景**（主内容区 `background: var(--bg-card-z1)`）

**为什么**：装饰层（宫殿/立绘）是 z-index 0/1 的背景层，半透明内容区等于文字直接坐在图片上；「透明 = 美观」只在内容层足够稀薄的界面上成立

**影响**：skin.css 当前原则——内容承载区一律不透明，装饰只出现在卡片容器伪元素层；对比度检查全绿（light/dark 各 28 项）

**何时重新考虑**：若未来做「沉浸模式」，应使用 backdrop-filter + 足够高的背景不透明度，而不是裸 transparent

---

## D-021 — skin.css 彻底去 token：颜色全部移入 token-mapping

**问题**：早期 skin.css 里手写大量 hex 颜色（待改造状态），与 token-mapping 生成的变量重复且互相打架

**演进**：`skin.css` 彻底去 token——**所有颜色值从 skin.css 移除，统一由 token-mapping.ts 生成 CSS 变量**，skin.css 只保留组件规则（布局、伪元素装饰、选择器、display 调整），引用 `var(--...)`

**为什么**：颜色与映射职责单一化——改颜色改 token-mapping（重新 build-mapping 即可），改布局改 skin.css；避免「两处定义、一处更新」的漂移

**影响**：skin.css 缩到 ~90 行；token-mapping.css 成为唯一的颜色权威

---

## D-022 — 弹窗/辅助面板不渲染装饰层

**问题**：主卡片选择器会误命中弹窗（model picker、设置）和侧边辅助面板

**选择**：装饰层选择器显式排除：
- qwenwork：排除 `right-dock-panel` / `aux-panel`
- opencode：排除 `[role="dialog"]` 内元素 + `[data-component]` + `button`

**为什么**：弹窗有自己的层叠上下文和背景，叠装饰会挡内容；辅助面板不是主卡片，不需要宫殿/立绘

**影响**：两个 skin.css 的选择器都带 `:not()` 排除链（由实时 DOM 选择器审计驱动——该审计支持已移除，页面结构信息不再随仓库维护）

---

## D-023 — 多主题并存：<name>.theme/ 目录 + 主题只在 build-tokens 选择

**问题**：skin-assets 之前只有单套素材（`original-images/manifest.json`），要并存多个主题（如深海女仆工坊 + 巧克力猫娘）时怎么组织？

**演进**：
- 之前：`skin-assets/original-images/` + 单一 `manifest.json`，换主题 = 改文件结构
- 现在：`skin-assets/` 下每个 `<name>.theme/` 目录是一个主题，各自带 manifest.json（schema 不变：4 个标准角色 + colorSource）
- **主题选择唯一入口是 build-tokens**：`--theme <name>` 参数 > `SKIN_THEME` 环境变量 > TTY 交互式编号菜单 > 非 TTY 报错列出主题。选定主题写入 `dist/tokens.json` 顶层 `theme` 字段，preview / apply 等其他模块经 `getActiveTheme()` 跟随，**不提供任何主题选择入口**

**为什么**：
- 交互式选择（而非固定默认/自动第一个）：用户明确要求无论几个主题都提示选择；确定性优先于便利，避免 CI 脚本或手误静默打到错误主题
- **其他模块不选主题**（用户明确约束）：主题是一个「构建期状态」，由 build-tokens 一次选定，下游消费（取色预览、打补丁注入立绘）自动跟随——避免每个命令各持一份主题状态导致不一致
- colorSource 条目同时支持**角色 key** 与**主题目录内文件名**：新 manifest 实际写的是文件名（更自由，可把任意图声明为取色源），但旧契约（角色 key）保持兼容，两种写法等价
- 单一活动 `tokens.json`（而非按主题分文件）：token-mapping → build-mapping → apply 的整条 CSS 链路都会把 tokens 烤进产物，按主题分文件需要重构三个包的映射管线；当前「换主题 = 重跑 build-tokens --theme X」的流程已足够

**影响**：`assets-loader.ts` 的加载函数显式收 `theme` 参数；`getActiveTheme()` 从 tokens.json 读活动主题；`selectTheme()` 只被 build-tokens CLI 调用；preview / apply 保持原参数面（无 --theme）

**何时重新考虑**：如果出现「预先 build 全部主题、apply 时即时切换」的需求，再评估 `dist/tokens/<theme>.json` 分文件布局与 token-mapping 管线改造

---

## D-024 — data URI 内联必须压缩：Chromium 的 2MB URL 上限

**问题**：nekopara 主题的背景图（2.8MB PNG）注入后不显示，立绘正常——同一次注入为何只有背景失败？

**根因**：**Chromium 的 URL 解析器有 2MB 硬上限（`url::kMaxURLChars = 2 * 1024 * 1024`）**，超过的 URL 被**静默替换为无效 URL**（Mojo IPC 边界直接丢弃，无报错）。data URI 也是 URL——背景图 base64 后 3.8MB > 2MB → 浏览器静默丢弃；立绘 92KB < 2MB 正常。maid-atelier 的背景是 webp 小图（~360KB）所以从未触发。

**选择**：bootstrap 构建时在 `imageFileToOptimizedDataUri()` 做**预算式压缩**：
- data URI < 1.5MB（安全余量）→ 原样内联，零质量损失
- 超限 → sharp 缩放（上限 1920×1080，`withoutEnlargement`）+ 转 webp（quality 82）再内联
- 压缩后仍比原图大 → 退回原图

**为什么**：压缩在构建时一次完成（sharp 本就是 skin-core 依赖），运行时零成本；小图不受影响（maid-atelier 全量原样）；2.8MB PNG 背景压到 ~0.34MB webp，注入脚本 7.8MB → 0.87MB，补丁更快、OOM watchdog 风险更低

**影响**：`buildBootstrap` / `buildImageInjectionScript` 变为 async；两处 patch-asar 调用点加 `await`；任何新主题的图片只要 > 1.5MB 都会被自动压缩，主题作者无需感知

**何时重新考虑**：如果某张图压缩后仍有明显画质损失（quality 82 不够），可引入按角色分级（背景 q82 / 立绘 q85+）或提高上限——但 2MB 是硬墙，不能突破

---

## D-025 — Codex Desktop 为什么走「官方 chrome 配置 + CDP 运行时注入」而不是 asar 补丁？

**问题**：Codex Desktop（Windows）= MSIX 商店包（C:\Program Files\WindowsApps，BUILTIN\Users 只读，直接启动 exe 被应用容器拒绝）；asar 内 5688 条目但仅 webview/index.html 一个 HTML 锚点（受保护目录内）；主 UI 由 app:// 自定义协议加载本地 Vite bundle。

**可选方案**：A. asar 补丁（copy-out + 主 bundle 手术）B. CDP 运行时注入（Fei-Away/Codex-Dream-Skin 生态主流，MIT）C. 官方外观通道（config.toml [desktop] appearance*ChromeTheme 键 + codex-theme-v1 导入串）

**选择**：B 为主通道 + C 为辅通道（A 明确不做）。

**为什么**：A 需复制 ~800MB 受保护目录、对 2.4MB minified 主 bundle 做偏移手术、每次 Store 更新即失效（且直接启动被拒，COM 激活无法注入环境变量）；B 不碰二进制、更新安全、可回环回滚，G1 门禁实测通过（COM IApplicationActivationManager 传参 --remote-debugging-port=9335 可行）；C 是官方支持通道（G2 实测仅影响原生窗口表面/背景，UI 内容不变色，故作辅）。代价：皮肤为运行时注入，会话内生效，用户自行启动 Codex 时只有原生 chrome 层，重跑 apply 即可（Non-Goal #5 语义一致）。

**影响**：skins/codex 的 patch 引擎 = config.toml 段编辑器（官方层）+ CDP 注入器（主层）；新增依赖 ws（CDP WebSocket 客户端，精确版本）。

---

## D-026 — config.toml 补丁的最小写入原则

**问题**：用户 config.toml 含大量个人键（模型、插件、项目、桌面外观偏好），皮肤只该碰外观色键。

**选择**：行级段编辑——只改 [desktop.appearanceLight/DarkChromeTheme] 及其 .semanticColors 子段的颜色键（accent/ink/surface/diffAdded/diffRemoved/skill）；contrast/opaqueWindows/fonts/codeThemeId/appearanceTheme 等一律保留；段外字节零改动（CRLF/BOM 原样保留）。marker 幂等；.skin.bak 双重身份（回滚 + pristine，D-010 延伸）；--force 从 pristine 还原再打、备份污染即拒绝。

**为什么**：对比度/不透明窗/字体是用户的材质偏好不是配色；最小写入保证还原语义精确（.bak 覆盖 = 完全还原）且不覆盖用户在应用内的任何设置。

---

## 重新考虑的时机（汇总）

- 如果加第 3 个 target（如 Cursor / Claude Code），评估是否抽共享 patch-asar 引擎（当前两套策略差异大，暂不统一）
- 如果用户要多套皮肤热切换，考虑 tokens.json 的版本控制策略
- 如果取色算法要换（如语义色），考虑在 skin-core 加算法选择参数
- 如果 `@electron/asar` 修复 unpacked 缺失 bug，QwenWork 可改 extract/repack
- 如果立绘数量增加到 10+ 张，data URI 体积问题凸显时重议素材加载方式
- 如果目标 app 升级改变了入口文件结构，更新 D-015 / D-008 对应的注入锚点
- 如果 CDP 运行时注入成为多 target 模式（skins/codex 为第一例），评估抽取共享注入器（参考 D-025）
