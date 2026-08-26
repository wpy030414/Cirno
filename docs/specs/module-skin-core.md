# Spec — skin-core 取色与 token 生成管线

**对应模块**：`packages/skin-core/src/`（`extract-colors.ts` / `generate-tokens.ts` / `types.ts` / `palette-preview.ts` / `index.ts`）

## 要实现什么

从图片提取主色（k-means++ 聚类），生成 **46 个 design tokens 的 light/dark 双套完整方案**，并提供可视化预览。

## 行为应该是什么

运行 `pnpm build-tokens` 时：

1. 取色来源：`--sources` 指定的图片；未指定时读取**选中主题** manifest 的 `colorSource`（只从角色立绘采样）。主题通过 `--theme <name>` / `SKIN_THEME` 环境变量指定；未指定时 TTY 下交互式编号选择，非 TTY 报错并列出可用主题。**主题选择只发生在 build-tokens**——选中主题写入 tokens.json 顶层 `theme` 字段，preview / apply 跟随（它们不选主题）
2. 每张图 sharp 解码 → 100x100 resize（`fit: inside`）→ 去 alpha → 每 2 像素采样
3. 单图 k-means++（k=16，确定性 PRNG）→ 多图簇合并二次 k-means（k=16）
4. 输出簇按 L* 亮度升序（darkest first）
5. 簇分类（饱和度阈值 0.25）→ 生成 light/dark 双套 46 tokens
6. 写入 `dist/tokens.json`（`{ light: {...46}, dark: {...46} }`）
7. `pnpm preview` 生成 `dist/palette.html`（簇卡片 + light/dark 双套 token 网格）

## 输入

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `--sources` | `string[]`（逗号分隔） | 选中主题的 manifest colorSource | 图片路径（webp/png/jpg） |
| `--theme` | `string` | 无（TTY 交互选择 / 非 TTY 报错） | 主题名（skin-assets 的 `<name>.theme/`），也支持 `SKIN_THEME` 环境变量 |
| `--out` | `string` | `dist/tokens.json` | 输出路径 |
| `--k` | `number` | `16` | 聚类数 |

## 输出

```json
{
  "theme": "maid-atelier",
  "char-config": {
    "character-left": { "offset": ["0%", "0%"], "height": "86%" },
    "character-right": { "offset": ["-30px", "-20%"], "height": "80vh" }
  },
  "light": { "neutral-50": "#f7f7f7", "...": "...", "accent": "#..." },
  "dark":  { "neutral-50": "#f7f7f7", "...": "...", "accent": "#..." }
}
```

顶层 `theme` = 活动主题（preview / apply 跟随读取；`--sources` 显式指定时无此字段）。顶层 `char-config` = 角色展示配置（offset 距边/距底 + 高度，均为 CSS 值字符串），取自 manifest 同名字段、补全为完整两角色配置（缺省：offset `["0%", "0%"]`、height `"86%"`）；供 apply 时生成 `--character-*-height` / `--character-*-position` CSS 变量。

**46 个 key**（`TOKEN_KEYS`，缺一不可）：
- neutral-50 ~ neutral-1100（12）
- brand-100 / 300 / 500 / 600 / 700 / 900（6）
- success / warning / critical / info × weak / base / strong（12）
- text-strong / base / weak / weaker / inverse（5）
- surface-base / raised / strong / weak（4）
- border-base / weak / strong / focus（4）
- input-base / active（2）
- accent（1）

## 约束

- **确定性**：k-means++ 初始化使用 mulberry32 PRNG，种子从前 64 个采样像素推导——相同输入 → 相同输出
- 簇数 < 8、中性簇 < 2、彩色簇 < 2 时报错
- 亮度排序按 L*（CIE LAB 简化公式），不是 RGB 平均
- **所有 token 输出 6 位不透明 hex**（禁止 alpha 通道）
- 100x100 resize 与每 2 像素采样是性能约束，不调默认值

## 算法要点（generate-tokens.ts）

- **中性色阶**：固定明度范围 [0.97 → 0.06]（不用图片实际范围，否则无真白/真黑、对比度失败）；色相取最亮中性簇；饱和度 **clamp 至 0.08**（角色图肤色/发色会染粉中性面）
- **brand 色阶**：最饱和彩色簇为种子；light 明度范围 [0.88 → 0.15]，dark [0.95 → 0.32]（保证链接/强调 ≥4.5:1）
- **语义色**：按色相匹配（绿 80-160 / 黄 30-80 / 红 <30 或 ≥340 / 蓝 200-280）；过滤 `s < 0.25 || l < 0.25 || l > 0.75` 的簇；未命中用 fallback（light/dark 各自调优）
- **accent**：与 brand 色相差 > 0.1 的彩色簇；无则取中位簇
- **light/dark 方向**：text 取 neutral 深端（light）/ 浅端（dark）；surface 取相反；semantic weak/base/strong 的明度方向 light/dark 相反；border 在 light 取深半区、dark 取浅半区（UI 对比 ≥3:1）

## 边界条件

1. **无 `--sources` 且选中主题的 manifest 无 colorSource**：报错（manifest 必须声明非空 colorSource 数组）
2. **无 `--sources` 且未指定主题**：TTY 交互选择；非 TTY 报错列出可用主题（详见 module-assets spec）
2. **图片为空 / 打不开**：sharp 抛错，CLI 退出码 1
3. **像素数 ≤ k**：直接返回像素本身为簇
4. **几乎无灰的图**：借最不饱和的彩色簇补中性（保证中性阶仍跨明暗）

## 验收标准

- [x] `pnpm build-tokens` 输出 light/dark 各 46 个 token，key 与 `TOKEN_KEYS` 完全一致
- [x] 相同输入两次运行输出完全一致（确定性）
- [x] light/dark 对比度达标：正文 ≥4.5:1、大文本/UI ≥3:1（两渠道 28 项检查全绿）
- [x] `pnpm preview` 生成 palette.html，浏览器可打开
- [x] 角色立绘颜色主导主题（背景图不参与取色）

## 如何判断任务已经完成

`pnpm build-tokens && pnpm preview` 全通，取色结果符合主题预期（角色主导、中性不偏色、语义色正确）。
