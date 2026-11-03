# 千问 / 百炼品牌图标

2026-11-03 从两个官方网站的页头标志确认并提取。灰白及单色版是本项目为主题适配制作的衍生资源，并非官方发布的版本；品牌标志及商标权利归原权利人。

| 品牌 | 官方页面 | 官网实际引用的 SVG 原件 | 仓库原件 |
| --- | --- | --- | --- |
| 千问 AI 平台 | https://www.qianwenai.com/ | https://img.alicdn.com/imgextra/i3/O1CN01XHTYyR1s7MFn6IMwj_!!6000000005719-55-tps-128-28.svg | `qwen-wordmark-official.svg` |
| 阿里云百炼 | https://bailian.console.aliyun.com/ | https://img.alicdn.com/imgextra/i1/O1CN01IU2US71Ciicsi3Br3_!!6000000000115-55-tps-357-76.svg | `bailian-wordmark-official.svg` |

应用使用的透明底矢量资源：

- `qwen-color.svg` / `bailian-color.svg`：保留官方渐变或各分面色彩，用于浅色主题。
- `qwen-white.svg`：千问白色轮廓，用于深色主题（程序绘制时用柔和白色 `#F2F2F4`）。
- `bailian-dark.svg`：百炼灰白分面，用于深色主题。保留官方的七个几何面，以六档灰白区分折面，不合并为整块剪影。

另外保留通用单色版本：

- `<品牌>.svg`：`currentColor` 填色，便于在网页中随主题使用。
- `<品牌>-black.svg`：纯黑色轮廓。
- `<品牌>-white.svg`：纯白色轮廓。

制作方法：从官网页头 SVG 中提取图形，去除品牌文字与冗余的重复路径，不改变图形路径坐标；以图形的实际边界居中，并在正方形 viewBox 内保留 5% 边距。彩色版保留官方颜色和渐变；百炼深色版逐面替换为灰白颜色，保留分面。通用单色版才把各面合入复合路径，避免同色接缝处出现抗锯齿细线。

`QuotaWidget.cs` 的 `IC_QWEN_COLOR` / `IC_BAILIAN_COLOR` / `IC_BAILIAN_DARK` 为相应 SVG 经 Chromium Canvas 渲染的 128×128 透明 PNG，再以 base64 内嵌。`BrandIcons.Draw` 直接绘制这些主题专用变体，保留色彩和明暗；`IC_QWEN` / `IC_BAILIAN` 保留为通用单色源。应用运行时不读取资源文件。
