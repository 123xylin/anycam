# RapidRobot 的 AnyCAD SDK 基线与本地变更

## 正式包基线

- NuGet.org：[AnyCAD.Rapid.NET 2026.9.22.1736](https://www.nuget.org/packages/AnyCAD.Rapid.NET/2026.9.22.1736)、[AnyCAD.Simulate.NET 2026.9.22.1736](https://www.nuget.org/packages/AnyCAD.Simulate.NET/2026.9.22.1736)。版本时间标记按北京时间 **2026 年 9 月 22 日 17:36** 记录。
- `feat/robot-tree` 使用这两个相同版本，`NuGet.config` 仅保留 NuGet.org。Release x64 可独立还原、构建及运行自测，无需 `../platform`。
- 正式包的 `SimulateInstance.Initialize()` 返回 `void`，没有 `GetInitializationDiagnostic()`。当前包上直接调用初始化会卡在 Physics 插件加载；程序显式加载 Plan/FCL 插件，并设置 OMPL/FCL 默认后端。包版自测因此覆盖该后端组合。此现象是本机运行验证结果，不推断其他环境均如此。

## 晚于包时间标记的 platform 提交

各子仓库提交时间已按北京时间比较。此处记录候选本地新特性，不把源码提交或离线包视为 NuGet.org 正式发布。

| 范围 | 提交与时间 | 变化 | 分类 |
| --- | --- | --- | --- |
| Simulate | `530df41`，9 月 22 日 17:42 | 调整 Kernel 初始化构建并移除 DLLMain 初始化代码 | SDK 运行时 |
| Robot | `f886e69`，9 月 22 日 17:43 | AnyRobot / AnyCAM 直接链接 Simulate Kernel，调整插件清单 | 应用集成 |
| UX | `50b0bd7`，9 月 22 日 17:44 | AnyDesigner 链接 Simulate Kernel，调整插件清单 | 应用集成 |
| Swig | `d267ecf`，9 月 22 日 21:27 | 修复首次 CMake configure 找不到 `SWIG_EXECUTABLE` | 构建工具 |
| DotNETAPI | `7b32a3d`，9 月 22 日 21:55 | 合入确定性 SWIG C# 生成相关修复 | 构建工具 |
| Simulate | `a7c0f55`，9 月 23 日 12:40 | `SimulateInstance.Initialize()` 改为返回 `bool`，增加初始化诊断及单测 | **打包后 SDK API** |
| DotNETAPI | `52c9af9`，9 月 23 日 12:59 | .NET Host 与 WinForms 示例改为链接 Simulate 并调整插件加载；SWIG 的具体 IK 类型导出调整 | SDK 集成与示例 |
| Simulate | `a371802`，9 月 23 日 14:56 | AnyRobotSimple 链接 Simulate Kernel | 示例构建 |
| DotNETAPI | `a650b02`，9 月 23 日 15:01 | 增加 `GlobalInstance.EnableRendering` 控制，调整 WinForms 渲染示例 | SDK API 与示例 |
| DotNETAPI | `03b41fe`，9 月 23 日 15:36 | WinForms 机器人示例的资源释放调整 | 示例 |

`../platform` 当前还有大量未提交的子模块指针、源码及其他工作文件；这些均不算正式包内容，也未被本仓库提交修改。`../platform/Nuget/package/offline/2026.9.23.1213/packages` 下存在同版本号的本地离线包，供 `dev` 验证打包后 API 使用，不能当作 NuGet.org 包版基线。

## 分支用途

`feat/robot-tree` 保持为面向 `master` 的包版 MR 分支。原 `dev` 的文件树与共同父提交相同，当前分支已包含其有效改动，不需要 cherry-pick。`master` 不在本次直接改写；待 MR 合入后才取得包版功能。

当前分支合入 `dev` 后，`dev` 使用本地 `2026.9.23.1213` 包增加 `bool Initialize()` / 初始化诊断适配，形成独立提交。其余后续 SDK API 仍以正式包支持情况为边界，避免泄漏到包版 MR。
