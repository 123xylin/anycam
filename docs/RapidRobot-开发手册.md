# RapidRobot 开发手册

> 适用分支：`feat/robot-tree`；正式 SDK：`AnyCAD.Rapid.NET` 与 `AnyCAD.Simulate.NET` `2026.9.22.1736`。本文解释仓库中的 WinForms RapidRobot；Avalonia 示例在文末定位。

## 1. 获取、构建与运行

环境为 Windows x64、.NET 8 SDK 和可访问 NuGet.org 的包源。仓库的 [NuGet.config](../NuGet.config) 用 `<clear />` 清除机器包源，只保留 NuGet.org；[RapidRobot.csproj](../WinForms/RapidRobot/RapidRobot.csproj) 把两个 AnyCAD 包固定在同一时间版本，目标是 `net8.0-windows`、WinForms 和 x64。项目将 `Model` 下的 URDF/STL 复制到输出目录。无需构建 `../platform`。

```powershell
dotnet restore WinForms\RapidRobot\RapidRobot.csproj --source https://api.nuget.org/v3/index.json
dotnet build WinForms\RapidRobot\RapidRobot.csproj -c Release --no-restore
.\WinForms\bin\Release\net8.0-windows\RapidRobot.exe
```

从「Open」选择输出目录的 `Model\Kr500\index.urdf`。其他自带模型有 `3A`、`dual_slide`、`shared_gantry_dual_6r`。`WinForms\AnyRobot.WinForms.sln` 只含 RapidRobot。Release 输出路径来自项目的 `OutputPath`；不要假设可执行文件位于项目目录的 `bin` 下。

## 2. 项目构成与调用链

| 文件 | 职责与主要入口 |
| --- | --- |
| [Program.cs](../WinForms/RapidRobot/Program.cs) | WinForms 与 Foundation/Exchange 初始化，加载 Plan/FCL 插件，设置 OMPL/FCL 默认后端，分派 GUI 或两个自测命令。 |
| [MianForm.cs](../WinForms/RapidRobot/MianForm.cs)、`MianForm.Designer.cs` | 主窗口、URDF 打开、场景显示、菜单、窗体生命周期和重绘。文件名 `MianForm` 是现有拼写，类名为 `MainForm`。 |
| [RobotControler.cs](../WinForms/RapidRobot/RobotControler.cs) | 单个 `RobotBody` 的 Arm/关节读取、点动、末端 IK、复位、显示及轨迹粒子。 |
| [RobotIndicatorForm.cs](../WinForms/RapidRobot/RobotIndicatorForm.cs)、`RobotIndicatorForm.Designer.cs` | 关节/Arm 控制面板；调用控制器并通过 `OnUpdate` 请求重绘。 |
| [RobotTargetEditor.cs](../WinForms/RapidRobot/RobotTargetEditor.cs) | 交互式末端目标编辑，预览变换交给控制器求 IK。 |
| [SimulationSdkForm.cs](../WinForms/RapidRobot/SimulationSdkForm.cs) | 后端列表、单关节规划、全末端 IK 检查及播放的 SDK 窗口。 |
| [UrdfVisualMesh.cs](../WinForms/RapidRobot/UrdfVisualMesh.cs) | 解析 Link 的 URDF visual STL，转换成 Link 局部坐标的 `CollisionTriangleMesh`。 |
| [CollisionDemoModel.cs](../WinForms/RapidRobot/CollisionDemoModel.cs) | 当前会话的确认凸包、候选预览、场景盒、豁免对；把它们写入规划请求。 |
| [CollisionDemoForm.cs](../WinForms/RapidRobot/CollisionDemoForm.cs) | V-HACD、场景实体、豁免和 Kr500 末端避障的交互窗口。 |
| [SimulationSdkAcceptance.cs](../WinForms/RapidRobot/SimulationSdkAcceptance.cs)、[CollisionDemoAcceptance.cs](../WinForms/RapidRobot/CollisionDemoAcceptance.cs) | SDK 与 Kr500 的无界面验收。 |

主路径是 `Program.Main → MainForm → RobotIO.Load → RobotControler/SimulationSdkForm/CollisionDemoForm → RobotSimulationSession → RobotTrajectoryPlayer`。`MainForm` 持有当前机器人和表单；打开新 URDF 或新建场景时先关闭附属窗口、取消待执行任务，再清理场景。`ModelChanged` 将控制器运动、碰撞预览 Link 变换和渲染重绘连在一起。

## 3. SDK 启动与后端

`Program.Main` 先初始化 `GlobalInstance` 和 `ExchangeInstance`。在正式 `2026.9.22.1736` 包上，本机直接调用返回 `void` 的 `SimulateInstance.Initialize()` 时，Physics 插件加载会卡住。程序因此从输出目录的 `runtimes\win-x64\native` 显式加载 `AnyCAD.Simulate.FCL.dll` 与 `AnyCAD.Simulate.Plan.dll`，通过 `SimulationBackends.LoadPlugin` 注册，再把 Planner/Collision 默认值设为 `OMPL`/`FCL`。失败会写入 Debug 输出；窗口中实际规划失败时显示后端诊断。不要将后续本地包的 `bool Initialize()` 或 `GetInitializationDiagnostic()` 写入包版代码。

`SimulationSdkForm` 用 `SimulationBackends.List(kind, true)` 列出可用和不可用后端，显示不可用原因；用户选择后构建 `BackendSelection`。IK、规划和碰撞后端是不同种类，名称不可互换。官方包验收覆盖 KDL/LMA/RL IK 和 OMPL/FCL 规划；本机无法完成的 Physics 初始化不等于这些已验证后端不可用。

## 4. 机器人模型、坐标与控制

`MainForm` 以 `RobotIO.Load(Path)` 读取 URDF，要求结果包含至少一个 Arm，再将 `RobotBody` 加入 `RenderControl`。主窗体记录原 URDF 路径，供碰撞示例读取 visual STL。URDF 中的长度为米；SDK 默认 `RobotUrdfLoadOptions.LengthScale=1000`，`RobotBody` 的位置及示例窗体的位移和尺寸以毫米表示。旋转关节用弧度，连续关节不按 `[-π, π]` 截断。

`RobotModel` 保存 Link、Joint、Frame、Arm 的拓扑；`RobotBody` 保存当前关节状态、世界变换与可视节点。`RobotLinkIndex` 等索引类型避免把四类索引混用。`RobotControler` 按 Arm 的路径偏移找到真正的 `RobotJointIndex`：固定关节拒绝点动；有界关节按限位钳制；连续关节允许越过 `2π`。设置位置后调用 `UpdateFrames()` 并触发 `ModelChanged`。`Reset()` 调用 `ResetToHome()`。

主窗体的 Move 命令创建末端目标场景节点与 `RobotTargetEditor`。目标先从世界坐标变换到机器人根 Link 坐标，再构造 `RobotSimulationTarget`，通过 `RobotSimulationSession.SolveIk` 解算。`RobotControler.TryMoveSelectedArmTip` 使用 `GetSourceState()` 与 `ApplyJointPositions`，在源状态变化时拒绝应用旧 IK 结果。Indicator 窗口、目标编辑器和 SDK 窗口都操作同一 `RobotBody`，因此运动会反映在主视图。

## 5. Link V-HACD 碰撞体

`CollisionDemoForm.Generate` 只处理当前选中 Link 的 URDF visual STL。`UrdfVisualMesh.Load` 支持二进制和 ASCII STL：读取 visual 中的 mesh scale 与 origin `xyz/rpy`，按 URDF 的 `Rz·Ry·Rx` 旋转顺序变换顶点，再乘 `RobotIO.Load` 默认的 1000 倍长度比例，输出 **Link 局部坐标**三角网格。无 STL、缺文件或无完整三角面会给出诊断；该示例目前不读取 DAE/OBJ，也不把生成结果写回 URDF。

AnyDesigner.exe 的“快速”预设落实为 `MaxConvexHulls=8`、`Resolution=100000`、`MinimumVolumePercentErrorAllowed=2.5`、`MaxRecursionDepth=8`、`MaxNumVerticesPerCH=32`，其余用 `VhacdOptions` 默认值（`ShrinkWrap=true`、`FillMode=FloodFill`）。`ConvexDecompositionSession.Compute` 在后台任务执行；WinForms 定时器读取进度，「Cancel」调用 `Cancel()`。结果成功且非空才进入黄色候选预览；「Confirm preview」把结果移入已确认集合并显示蓝色预览。「Delete」同时清掉候选和已确认结果；失败或取消不会覆盖之前确认的碰撞体。

`CollisionDemoModel.ApplyTo` 对每个已确认 Link 创建一个 `PlanningCollisionBody`，其中每个 V-HACD 结果是一个 `PlanningCollisionShape(Type=ConvexHull)`；使用 Link 的当前世界变换，并用 `RobotCollisionBinding` 绑定相应 `RobotLinkIndex`，`LinkToBody` 为单位阵。一个 Link 的多个凸包因而共同参与碰撞计算。预览场景节点使用同一批顶点/三角索引，随 `UpdateLinkTransforms` 跟随机器人。再次应用请求时会清掉本模型之前写入的碰撞体和绑定，避免重复。

## 6. 场景实体与碰撞豁免

演示场景实体是运行期盒体，记录 ID、名称、中心 `X/Y/Z` 和 `Width/Depth/Height`。默认 Kr500 盒体中心为 `(2750,0,1470)` mm，尺寸为 `300×300×400` mm。可见橙色盒由 `GeometryBuilder.CreateBox` 构造；规划端由相同参数构造 `Environment` 类型 `PlanningCollisionBody` 和 `Box` 形状，并把静态世界位姿纳入场景指纹。添加、修改、删除后重新规划即可生效。

模型根据 `RobotModel` 的每条 Joint 建立父子 Link 默认豁免。界面列表明确标出相邻 Link，并允许「Check pair」取消、「Ignore pair」恢复，也允许对其他实体对增删豁免。每次 `ApplyTo` 清空请求中的 `IgnoredPairs`，只将当前确实存在于碰撞场景的双方写回，因此界面与实际规划没有两个独立的豁免来源。Owner ID 对 Link 使用 `index+1`，场景实体从 `1000000` 起分配；删除实体时同时删除关联豁免。

## 7. Kr500 末端避障、诊断与播放

打开 Kr500 的「Collision demo」会设置演示姿态：AXIS2 `-0.45 rad`、AXIS3 `0.6 rad`，并创建默认障碍物。先生成并确认 AXIS2、AXIS3、AXIS6 的凸包；未确认任何 Link 凸包时窗口拒绝规划。默认目标是当前末端沿世界 X 轴 `-450 mm`，保留姿态，用户可改 X/Y/Z。

规划顺序如下：

1. 读取 Arm 0 的 Tip Frame 世界变换；把用户输入的世界偏移与其相乘，再转换成根 Link 坐标的 `RobotSimulationTarget`。
2. `RobotSimulationSession.SolveIk` 求完整手臂关节目标；IK 不成功时直接显示 `Status` 和 `Diagnostic`，不沿用旧目标。
3. 为每个解得的关节创建 `RobotJointPosition` 和正的速度/加速度限制，调用 `CreatePlanningRequest`；将 `Options.CollisionResolution` 设为 `0.05 rad`。正式包默认 `1 rad` 对小幅关节运动过粗，会漏掉中途障碍。
4. `CollisionDemoModel.ApplyTo` 从已确认凸包、当前盒体及豁免重建碰撞请求；后台调用 `Plan`。起点/终点碰撞、超时、取消、后端缺失或目标不可达时显示规划状态与诊断，不加载失败结果。
5. 成功结果交给 `RobotTrajectoryPlayer.Load`；「Play」「Pause」「Stop / cancel」及时间滑块调用 `Play/Pause/Stop/Seek`。定时器用真实经过时间调用 `Tick`，并更新凸包预览变换与渲染。

停止播放使机器人返回轨迹起点。关闭窗口取消任务并释放播放器、后端和预览；重新加载机器人时主窗体关闭旧窗口并创建新的 `CollisionDemoModel`。凸包、场景盒和豁免只在窗口会话中保存，**没有 JSON 保存或 URDF 写回**。若实际机器人已被其他界面移动，重新打开演示窗口或重新计算目标，避免把旧规划当成当前状态。

## 8. 自测与排障

```powershell
$exe = '.\WinForms\bin\Release\net8.0-windows\RapidRobot.exe'
$models = 'C:\absolute\path\to\anyrobot\WinForms\bin\Release\net8.0-windows\Model'
& $exe --sdk-self-test 'C:\temp\sdk-report.txt' $models
& $exe --collision-self-test 'C:\temp\collision-report.txt' $models
```

可执行文件是 GUI 子系统程序；以报告文件和进程退出码判断结果。SDK 自测覆盖 FK、V-HACD、KDL/LMA/RL IK、轨迹时间参数化与采样、OMPL/FCL 规划及播放、不可用后端、连续关节、四个 URDF。碰撞自测覆盖 AXIS2/AXIS3/AXIS6 各 8 个凸包、相邻豁免改变检测结果、直达路径中点受阻、4 点绕行及 9 个采样状态、盒体或 Link 凸包删除/恢复、Kr500 播放和新会话状态重置。自测通过不替代人工检查预览颜色、窗口布局、拖动及截图。

| 现象 | 首先检查 |
| --- | --- |
| URDF 打不开 | 文件路径、相对 STL 路径、是否有 Arm；查看 `RobotIO.Load` 异常。 |
| Link 无法生成凸包 | 选中的 Link 是否有 STL visual；文件是否为完整三角网格；`ConvexDecompositionSession.IsAvailable()` 与结果诊断。 |
| IK `NoConvergence` | 目标是否可达，姿态是否过约束，Kr500 是否已在非奇异演示姿态。 |
| 规划 `InvalidStart/InvalidGoal` | 当前或目标状态与已确认 Link/盒体发生碰撞；检查相邻豁免和单位。 |
| 后端不可用 | 输出目录的原生 DLL、插件加载 Debug 记录和 `SimulationSdkForm` 的描述。 |
| 路径看似穿透 | 确认凸包已点击确认，检查 `CollisionResolution`、可见盒体与碰撞盒体是否来自同一实体参数。 |
| 重新打开后编辑消失 | 这是当前会话设计；目前没有持久化格式。 |

扩展网格格式应从 `UrdfVisualMesh` 入手；增加场景形状或持久化应扩展 `CollisionDemoModel` 与其预览/规划双向构造；换规划算法应检查 `SimulationBackends` 的能力描述与结果状态。不要直接在界面控件中缓存另一份碰撞请求。

## 9. SDK 包时间线与分支

NuGet.org 正式版本为 [Rapid.NET 2026.9.22.1736](https://www.nuget.org/packages/AnyCAD.Rapid.NET/2026.9.22.1736) 和 [Simulate.NET 2026.9.22.1736](https://www.nuget.org/packages/AnyCAD.Simulate.NET/2026.9.22.1736)；包名时间标记按北京时间 **2026 年 9 月 22 日 17:36** 记录。`feat/robot-tree` 面向 `master`，仅依赖正式包；`master` 待 MR 合入才取得更新。`dev` 包含该分支的功能，另有 `2026.9.23.1213` 本地离线包及打包后初始化 API 的专属适配。该离线包不是 NuGet.org 发布版。

| 晚于时间标记的提交 | 变化 | 性质 |
| --- | --- | --- |
| Simulate `530df41`，9/22 17:42 | Kernel 初始化构建调整、移除 DLLMain 初始化 | 运行时 |
| Robot `f886e69`，9/22 17:43；UX `50b0bd7`，17:44 | AnyRobot/AnyCAM/AnyDesigner 链接 Simulate Kernel 并调整插件清单 | 应用集成 |
| Swig `d267ecf`，9/22 21:27；DotNETAPI `7b32a3d`，21:55 | 首次 configure 与确定性 C# 生成修复 | 构建工具 |
| Simulate `a7c0f55`，9/23 12:40 | `Initialize()` 返回 `bool`，增加诊断接口 | **未进入正式包的 API** |
| DotNETAPI `52c9af9`，9/23 12:59 | .NET Host/示例的 Simulate 插件加载与 SWIG 调整 | 集成 |
| Simulate `a371802`，9/23 14:56 | AnyRobotSimple 链接调整 | 示例构建 |
| DotNETAPI `a650b02`，9/23 15:01；`03b41fe`，15:36 | `EnableRendering` 与示例资源释放调整 | API/示例 |

`../platform` 还有未提交源码与子模块改动，不属于正式包，也未被包版分支引用。这里的提交时间均按北京时间比较；详细的 SDK API 版本界限见 [AnyCAD.Simulate.NET 开发手册](AnyCAD.Simulate.NET-开发手册.md)。

## 10. 同仓库其他项目

`Avalonia\AnyRobot.Avalonia.sln` 包含 `AnyTube.NET` 和 `NCViewer.NET`：前者读取 `AnyTubeJsonLoader` 的管材 JSON 示例并由 `AnyTubeScene`/`PathAnimator` 展示；后者由 `NcFileParser` 解析 NC 文档并通过 `NcScene`/`ToolAnimator` 呈现。这两个项目使用 `AnyCAD.Rapid.Avalonia` 与较早的 `AnyCAD.Simulate.NET 2026.6.12.1032`，不是本手册的 RapidRobot 构建依赖，也不共享本次碰撞编辑模型。
