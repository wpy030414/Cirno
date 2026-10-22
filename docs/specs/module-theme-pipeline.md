# Spec — 主题管线（tools/theme → Theme.xaml）

## 要构建什么

一个**构建期**主题管线：输入一组插画与一份主题清单，输出可直接被 WinUI 3 加载的资源字典、
位图资产与主题注册表。运行期应用不依赖 Node。

产物同时服务两件事：内置「大肥鱼」主题的装配，以及未来新增主题的零代码扩展。

## 行为

```
themes/<id>.theme/manifest.json
  roles（4 个标准角色）+ colorSource（仅取色来源）+ displayName/description + art 参数
        ↓ 取色：k-means++，确定性种子，按 L* 排序
  颜色簇
        ↓ 生成
  46 个 token × { light, dark }
        ↓ 发射
  artifacts/themes/<id>/Theme.xaml      三套 ThemeDictionaries + Cirno* 全量调色板 + 主题元数据
  artifacts/themes/<id>/ThemeInfo.json  展示信息、资产 URI、代码侧最小色板
  artifacts/themes/<id>/Assets/*        背景 JPG、立绘 PNG
  tools/theme/dist/themes.json          主题注册表（应用的主题选择器读取）
```

### 46 token 契约

`neutral`（12 级）、`brand`（6 级）、`semantic`（4 类 × 3 档）、`text`（5）、`surface`（4）、
`border`（4）、`input`（2）、`accent`（1）。light/dark 各一套完整方案，全部为 6 位不透明 hex。

### XAML 发射规则

- 主题字典的键：`Default` = 深色（WinUI 约定，不是 `Dark`）、`Light`、`HighContrast`。
- 每个 Fluent 颜色资源同时发射 `X`（Color）与 `XBrush`（SolidColorBrush），因为控件样式两种都会引用。
- 46 个 token 额外全量发射为 `Cirno*Brush`，供侧边栏、时间线卡片、徽标等自绘部件使用。
- 高对比度字典不做品牌着色，统一退回系统颜色。
- 透明度通道政策：token 一律不透明；8 位带 alpha 的颜色只允许出现在组件级调色板中。

### 资产转换

- WinUI 的 `Image` 不支持 webp，因此构建期转码：背景 → JPG（无 alpha，限制最大宽度），立绘 → PNG（保留 alpha）。
- 资产以 `ms-appx:///Themes/<id>/Assets/...` 的路径被引用，应用按同一布局打包生成目录。

### 对比度闸门

构建时校验「强调文字/基础面」「次级文字/抬升面」「弱提示文字/基础面」在明暗两套下的对比度：
低于 3:1 直接失败，3–4.5 之间输出警告。

## 输入 / 输出

**输入**

- `themes/<id>.theme/manifest.json`：`id`、`displayName`、`description`、`art`（氛围层/立绘/遮罩不透明度）、
  `roles`、`char-config`、`colorSource`。
- 同目录下的源图（webp/png/jpg）。

**输出**

- `artifacts/themes/<id>/Theme.xaml`、`ThemeInfo.json`、`Assets/*`、`tokens.json`。
  该目录与 `ms-appx:///Themes/<id>/` 一一对应，由应用项目整体映射打包。
- `tools/theme/dist/themes.json`：`{ builtin, themes[] }`，内置默认主题不在此列（由应用内置）。

## 约束

- 主题选择只发生一次：命令行 `--theme <id>` 优先于环境变量 `CIRNO_THEME`，都没有且仅一个主题时自动选择。
- 取色只从 `colorSource` 声明的图采样；场景背景不得污染主题色调。
- 生成物不得手工修改；不得在源码中硬编码任何颜色值。
- 单张图片源文件只保留一份（webp），派生产物由构建生成且不纳入版本控制。
- 新增主题 = 新建目录 + 写 manifest，不改代码。

## 边界条件

- 主题缺角色或缺 `colorSource` → 构建失败并指出具体字段。
- 图片尺寸过大 → 按角色上限缩放，不报错。
- 目录名与 `id` 不一致 → 以 manifest 的 `id` 为准，`ThemeInfo` 与资产路径都用 `id`。
- 生成目录被删除 → 重新构建即可；应用侧若找不到主题资源，回落到默认主题。
- 高对比度系统主题 → 所有品牌色失效，界面保持可用（这是预期行为，不是缺陷）。

## 验收标准

- [ ] `pnpm theme` 一条命令产出 Theme.xaml / ThemeInfo.json / Assets / 注册表。
- [ ] 生成的 Theme.xaml 是合法 XML，三套字典齐全，资源键数量稳定（当前 490）。
- [ ] 明暗两套下正文对比度 ≥ 4.5:1，弱提示文字 ≥ 3:1。
- [ ] 主题注册表包含「大肥鱼」，且 `displayName` 正确。
- [ ] 应用加载该 XAML 后，标准控件（按钮、输入框、列表、菜单、Tab、InfoBar）全部跟随主题。
- [ ] 删除 `artifacts/themes/` 后重建，产物字节级一致（确定性）。

## 完成定义

新增一个主题只需放入素材与 manifest，跑一条命令即可在应用中切换，且全程没有手工调色与手工改 XAML。
