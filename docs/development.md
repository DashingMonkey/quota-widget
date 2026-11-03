# 参与开发

[返回首页](../README.md) · [文档目录](README.md)

项目使用 C# 与 WinForms，主程序为单文件源码，无第三方运行库依赖。

```powershell
.\build.bat
```

构建脚本调用 .NET Framework 自带的 `csc.exe`，在仓库根目录生成 `QuotaWidget.exe`。如果程序正在运行，脚本会先关闭旧实例，编译成功后重新启动。

| 文件 / 目录 | 用途 |
| --- | --- |
| [QuotaWidget.cs](../QuotaWidget.cs) | 窗口、供应商查询、配置存储与自定义接口 |
| [build.bat](../build.bat) | 本地编译脚本 |
| [assets/brands](../assets/brands/README.md) | 品牌 SVG、明暗主题变体与官方来源说明 |
| [tests](../tests) | 解析、界面行为与本地模拟接口测试 |

<details>
<summary><strong>运行服务商专项测试</strong></summary>

在仓库根目录运行：

```powershell
powershell -File tests\Run-GlmWalletTests.ps1
powershell -File tests\Run-MimoTests.ps1
powershell -File tests\Run-AliyunTests.ps1
```

这些测试使用模拟数据或本地接口，检查余额与套餐解析、供应商切换和 Cookie 获取取消等行为，不读取真实账号凭证。部分界面用例会短暂显示测试窗口，产物位于 `obj` 目录。

</details>

欢迎通过 [Gitee Issues](https://gitee.com/dashingmonkey/quota-widget/issues) 反馈问题或建议。反馈时请提供程序版本、供应商类型、复现步骤与错误提示，并移除敏感信息。

## 更新文档截图

在仓库根目录运行：

```powershell
powershell -File tools\Render-DocScreenshots.ps1
```

脚本编译独立的截图工具，将应用图标和四组深浅主题图片输出到 `docs/images/`。
界面由本项目的 WinForms 窗体与绘制方法生成，额度、余额和令牌均为演示数据；不会加载用户配置、查询真实接口或保存演示配置。生成时会短暂显示演示窗口。

[DocScreenshots.cs](../tools/DocScreenshots.cs) 中维护演示数据和截图场景，[Render-DocScreenshots.ps1](../tools/Render-DocScreenshots.ps1) 负责编译与导出。额度窗口按服务商返回的含义展示，示例数字不代表任何套餐的实际定价或配额。

现有综合行为测试脚本中的部分自定义接口用例仍引用旧配置字段，尚未完成迁移；上面的专项脚本可独立运行。
