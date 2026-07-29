# UI 系统

Pi Companion 的 UI 基础层同时覆盖 Vue Agent Chat 与 WPF 桌面外壳，目标是让字体、颜色、尺寸、焦点和交互语义只有一个维护入口。

## Design tokens

唯一手工维护的 token 源是：

```text
design/design-tokens.json
```

运行以下命令生成各渲染层资源：

```powershell
node scripts/generate-design-tokens.mjs
```

生成文件包括：

- `src/PiCompanion.Chat/src/color-tokens.css`
- `src/PiCompanion.Chat/src/typography.css`
- `src/PiCompanion.Chat/src/component-tokens.css`
- `src/PiCompanion.Desktop/Design/DesignTokens.xaml`
- `src/PiCompanion.Desktop/Design/GeneratedDesignTokens.cs`

不要直接编辑这些生成文件。Vue 生产构建和 WPF 构建都会执行 `--check`，源与生成结果不一致时构建失败。

## Vue 组件层

共享组件位于 `src/PiCompanion.Chat/src/components/ui`，由 `index.ts` 统一导出。业务 SFC 显式导入并使用：

- `UiButton`
- `UiInput`
- `UiTextarea`
- `UiNativeSelect`
- `UiSelect`
- `UiSwitch`
- `UiDialog`
- `UiMenu` / `UiMenuItem`

弹窗、菜单和开关基于 Reka UI 的无样式可访问性 primitive；视觉由项目 token 和 `ui-components.css` 控制。业务组件不应直接声明 `button`、`input`、`textarea`、`select`，也不应自行实现 `dialog` / `alertdialog` 语义。

`ui-component-boundaries.acceptance.test.ts` 会守住这些边界。新增控件时先扩展 `components/ui`，再由业务组件消费。

## WPF 主题

`App.xaml` 合并生成的 `DesignTokens.xaml`。`ThemeManager` 从生成的 C# 调色板切换深浅主题，并同步更新 Color 与 Brush 资源；不能消费 WPF 资源的绘图表面通过 `ColorDesignTokens` 使用同一调色板。

## 日常维护

修改 token 后：

```powershell
node scripts/generate-design-tokens.mjs
Set-Location src\PiCompanion.Chat
npm test
npm run build
Set-Location ..\..
dotnet build src\PiCompanion.Desktop\PiCompanion.Desktop.csproj
```
