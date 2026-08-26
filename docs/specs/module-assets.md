# Spec — skin-assets 素材库与 manifest

**对应模块**：`packages/skin-assets/` + `skin-core/src/assets-loader.ts`

## 要实现什么

所有 target 共用的图片素材库。素材库下可容纳**多个主题**（`<name>.theme/` 目录），每个主题拥有自己的 `manifest.json`（标准角色映射 + 取色来源声明），以及 skin-core 侧的加载器。

## 行为应该是什么

1. 每个 `<name>.theme/` 目录是一个独立主题，`manifest.json` 把 4 个**标准角色**映射到物理文件（角色 key 固定，文件名自由）
2. `manifest.json` 声明 **colorSource** 取色来源——**只有这些图喂给取色管线**；条目可以是**角色 key**（旧契约，如 `"character-left"`）或**主题目录内的文件名**（新用法，直接写 `<图片文件>` 名），两种写法都支持
3. skin-core 通过 `assets-loader.ts` 按角色（而非文件名）消费素材；所有加载函数**必须显式指定主题**
4. **主题选择只发生在 build-tokens**：`selectTheme()`（`--theme` 参数 > `SKIN_THEME` 环境变量 > TTY 交互选择 > 非 TTY 报错）只被 build-tokens CLI 调用，选定主题写入 `dist/tokens.json` 的顶层 `theme` 字段
5. **其他模块不选择主题**：preview / apply 等通过 `getActiveTheme()` 读取 tokens.json 的 theme 字段，跟随 build-tokens 选定的活动主题
6. bootstrap 构建时按活动主题的 4 个角色读取图片转 data URI

## 目录结构

```
packages/skin-assets/
├── <name-a>.theme/             ← 主题名 = 目录去 .theme 后缀
│   ├── manifest.json
│   └── <图片文件>（文件名自由，经 manifest 登记到角色）
└── <name-b>.theme/
    ├── manifest.json
    └── <图片文件>
```

theme 发现规则：`skin-assets/` 下以 `.theme` 结尾的目录且内含 `manifest.json`。**skin-assets 是管线之外的演示素材库——文档只定义结构契约，不绑定任何具体主题或文件名。**

## manifest.json 契约

```json
{
  "schema": "standard-roles-v1",
  "roles": {
    "background-day": "<file>",
    "background-night": "<file>",
    "character-left": "<file>",
    "character-right": "<file>"
  },
  "colorSource": ["character-left", "character-right"],
  "char-config": {
    "character-left": { "offset": ["0%", "0%"], "height": "86%" },
    "character-right": { "offset": ["-30px", "-20%"], "height": "80vh" }
  }
}
```

**标准角色**（固定 4 个，任何主题都必须提供映射）：

| 角色 | 说明 |
|---|---|
| `background-day` | 背景·昼 |
| `background-night` | 背景·夜 |
| `character-left` | 立绘·左 |
| `character-right` | 立绘·右 |

**colorSource 约定**：取色来源必须显式声明。条目两种写法等价：
- 角色 key：`["character-left", "character-right"]`
- 文件名：`["<图片文件>", "<图片文件>"]`（主题目录内任意图，不限于角色图）

场景背景（day/night）不得参与取色（会绑架主题色调）。变更取色来源只改 manifest，不改代码。

**char-config（角色展示配置，可选）**：键为角色 key（`character-left` / `character-right`），每个角色可配：
- `offset: [x, y]`（**CSS 值字符串二元组**，默认 `["0%", "0%"]`，如 `["-30px", "-20%"]`）——`x` = 距边缘（左角色距左、右角色距右），`y` = 距底；值原样透传进 `calc(100% - <value>)`，支持任意 CSS 长度/百分比（负值让立绘探出视窗，如半身像）。apply 时生成 `--character-*-position` CSS 变量（左角色 `x calc(100% - y)`、右角色 `calc(100% - x) calc(100% - y)`）
- `height`（**CSS 值字符串**，默认 `"86%"`，如 `"80vh"`）——立绘高度，生成 `--character-*-height` CSS 变量

未列出的角色与未配字段用默认值。build-tokens 读取后写入 tokens.json 顶层 `char-config`（补全为完整两角色配置），apply 时据此生成 CSS 变量，skin.css 以 `var()` 消费。校验：`offset` 必须为两个非空字符串，`height` 必须为非空字符串，非法值报错。

## 加载器 API（assets-loader.ts）

| API | 返回 | 说明 |
|---|---|---|
| `listThemes()` | `string[]` | 扫描 `.theme/` 目录，返回排序后的主题名 |
| `getThemeDir(theme)` | `string` | 主题名 → 物理目录；接受 `maid-atelier` 或 `maid-atelier.theme` 两种写法；不存在时报错并列出可用主题 |
| `selectTheme(themeArg?)` | `Promise<string>` | **仅 build-tokens 调用**：`--theme` 参数 > `SKIN_THEME` 环境变量 > TTY 交互编号菜单（回车默认第一项）> 非 TTY 报错；0 个主题直接报错 |
| `getActiveTheme()` | `string` | **其他模块跟随入口**：读 `dist/tokens.json` 顶层 `theme` 字段并校验主题存在；tokens.json 缺失或无 theme 字段 → 报错提示先跑 build-tokens |
| `getRoleImagePath(role, theme)` | `string` | 单个角色的物理路径 |
| `getAllRoleImagePaths(theme)` | `Record<StandardRole, string>` | 4 个角色 → 路径 |
| `getColorSourceImagePaths(theme)` | `string[]` | 仅 colorSource 声明的图（取色专用；角色 key 或文件名都解析） |
| `getCharConfig(theme)` | `Record<'character-left' \| 'character-right', { offset: [string, string]; height: string }>` | 角色展示配置（CSS 值字符串），manifest char-config 未列出的角色/字段补默认值（offset `["0%", "0%"]`、height `"86%"`） |

## 约束

- 纯静态资源：**不写代码**，只有图片 + 每个主题一个 JSON 映射
- 文件名自由：`<主题名>-<角色>-<版本>.<ext>` 是建议命名，**禁止**在 skin-assets 层强制任何主题前缀
- 版本号递增（v1 → v2 → v3）；WebP 优先；原图保持高分辨率不压缩
- manifest 的 4 个 `roles` 字段必须指向真实存在的文件
- manifest 缺失、`roles` 缺角色 key、或 `colorSource` 为空数组 → 加载器抛错
- **所有加载函数必须显式传主题**（`theme: string`）——无默认主题，杜绝静默选择

## 边界条件

1. **角色 key 缺失**：`getRoleImagePath` 抛错（`role 'X' not found in manifest.json`）
2. **colorSource 条目既不是角色 key 也不是存在的文件**：抛错（带主题目录路径）
3. **char-config 非法**（非对象 / offset 非两个非空字符串 / height 非非空字符串）：抛错
3. **多主题且 build-tokens 未指定**（TTY）：交互式编号菜单，输入数字 / 主题名 / 回车（默认第一项）
4. **多主题且 build-tokens 未指定**（非 TTY）：报错 `multiple themes available: ... — pass --theme <name> or set SKIN_THEME=<name>`
5. **0 个主题**：报错 `no themes found under packages/skin-assets/ — expected <name>.theme/ dirs with manifest.json`
6. **preview / apply 读取活动主题**：tokens.json 缺失或无 theme 字段 → 报错提示「先跑 pnpm build-tokens（主题在那里选择）」；theme 指向的目录被删除 → 报错列出可用主题
7. **新增主题**：新建 `<name>.theme/` 目录 + manifest.json + 图片即完成注册，无需改代码；`build-tokens --theme <name>` 直接可用
8. **新增素材**：只改文件名映射 + 版本号，角色 key 不动；扩展新角色需先提案扩展标准角色枚举（目前固定 4 个）

## 验收标准

- [x] `listThemes()` 返回全部主题（按名排序）
- [x] 每个主题 manifest 4 个角色全部指向存在的文件
- [x] `colorSource` 非空，角色 key 与文件名两种写法都能解析
- [x] `pnpm build-tokens --theme <name>` 能读到该主题的立绘路径并出 tokens，theme 字段写入 tokens.json
- [x] build-tokens 未指定主题时 TTY 交互选择 / 非 TTY 报错列出主题
- [x] preview / apply 不选主题，跟随 tokens.json 的 theme 字段
- [x] 无代码文件（只有图片、JSON、文档）

## 如何判断任务已经完成

新增/切换主题只需：建 `<name>.theme/` 目录 → 填 manifest（角色映射 + colorSource）→ `pnpm build-tokens --theme <name>` 选定活动主题 → `pnpm preview` / `pnpm apply` 自动跟随，无需改任何代码。
