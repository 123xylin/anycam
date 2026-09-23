# AnyRobot.NET

本仓库包含 WinForms `RapidRobot.exe`，以及 Avalonia 的 AnyTube.NET、NCViewer.NET 示例。RapidRobot 使用正式 NuGet.org `AnyCAD.Rapid.NET` 与 `AnyCAD.Simulate.NET 2026.9.22.1736`。

| 文档 | 用途 |
| --- | --- |
| [公众号文章](docs/RapidRobot-公众号.md) | 简洁展示 Kr500 碰撞体与避障流程，预留截图位置。 |
| [RapidRobot 开发手册](docs/RapidRobot-开发手册.md) | 构建、运行、架构、实现细节、验证与分支基线。 |
| [AnyCAD.Simulate.NET 开发手册](docs/AnyCAD.Simulate.NET-开发手册.md) | 正式包完整功能用法与公开 API 索引。 |

RapidRobot 构建入口：

```powershell
dotnet restore WinForms\RapidRobot\RapidRobot.csproj --source https://api.nuget.org/v3/index.json
dotnet build WinForms\RapidRobot\RapidRobot.csproj -c Release --no-restore
```
