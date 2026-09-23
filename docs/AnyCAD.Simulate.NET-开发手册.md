# AnyCAD.Simulate.NET 开发手册

> **API 基线：** NuGet.org `AnyCAD.Simulate.NET 2026.9.22.1736`，以包内 `lib/net80/AnyCAD.Simulate.NET.dll` 的公开类型和签名为准。本文是按任务查找的 .NET 工具手册，覆盖 RapidRobot 未使用的功能；不是 C++ Simulate 平台或 AnyCAD.Foundation.NET 的完整手册。`2026.9.23.1213` 本地离线包的差异见末节，不能当成正式发布 API。

## 1. 选择任务与依赖

| 要完成的事 | 入口 | 本手册章节 |
| --- | --- | --- |
| 装载后端、选择 IK/规划/碰撞实现 | `SimulationBackends`、`BackendSelection` | 2 |
| 读 URDF、构造 Link/Joint/Frame/Arm、运动机器人 | `RobotIO`、`RobotModel`、`RobotBody` | 3 |
| DH、独立 FK、单末端或树形 IK | `RobotDH`、`RobotFrameBuilder`、`FkEvaluator`、`IkSolver` | 4 |
| 关节路径、时标、采样与播放 | `RobotSimulationSession`、`JointTrajectoryTiming`、`RobotTrajectoryPlayer` | 5 |
| 定义规划碰撞场景与豁免 | `PlanningCollision*`、`RobotCollisionBinding` | 6 |
| 从网格生成多个凸包 | `CollisionTriangleMesh`、`VhacdOptions`、`ConvexDecompositionSession` | 7 |
| 场景节点或 Document 碰撞 | `CollisionSceneWorld`、`CollisionDocWorld` | 8 |
| 动画、曲线与刚体片段 | `Animation`、`RobotAnimation`、`RigidAnimation` | 9 |
| 铣削与增材仿真数据和会话 | `MillProgram`、`AdditiveProgram`、`ManufacturingSimulationSession` | 10 |
| 曲线/曲面绘图、调色、网格工具 | `Matplot`、`ColorLookupTable`、`MeshTool` | 11 |

包的 nuspec 声明依赖**同版本** `AnyCAD.Foundation.NET`，提供 .NET Framework 4.8、.NET 6、8、10 的程序集。以下代码以 Windows x64、.NET 8 为示例；其它目标框架的原生运行条件应在目标环境单独检查。`AnyCAD.Foundation` 中的 `Path`、`Matrix4d`、`Vector3d`、`SceneNode`、`ObjectId`、集合与渲染器是跨包类型。示例项目用 `AnyCAD.Rapid.NET` 提供 WinForms 图形界面，但调用无界面的机器人/规划值类型不要求先创建 Document/Element。

```xml
<PackageReference Include="AnyCAD.Simulate.NET" Version="2026.9.22.1736" />
```

本文的“公开”指程序集可通过 .NET 反射看到；**公开类型不一定有可由 C# 直接调用的构造器**。附录列出每个公开类型的构造、属性和操作签名；`(IntPtr, bool)` 构造是 SWIG 原生指针包装细节，不是使用者自行创建对象的方法。返回原生包装对象的调用通常应采用 `using`/`Dispose`；不要在释放父会话后继续使用由它返回的集合或视图。跨线程执行 `Compute`/`Plan` 时，保持输入网格、请求、会话和机器人在任务完成前有效，取消后等待任务结束再释放。

## 2. 初始化、插件与后端

`SimulateInstance.Instance().Initialize()` 在本**正式包**中返回 `void`，没有初始化诊断访问器。运行图形程序通常先初始化 Foundation/Exchange，再初始化或加载所需 Simulate 插件，退出时销毁 Foundation。后端分三类：`EnumSimulationBackendKind.Ik`、`Planner`、`Collision`。`SimulationBackends.List(kind, true)` 包括不可用描述，`Describe` 查询指定名称，`Resolve` 补全选择，`SetDefaults` 设置默认，`LoadPlugin(path)` 装载原生插件。不要把“包中有 DLL”当成“该后端已注册且可用”。

```csharp
using var found = SimulationBackends.List(EnumSimulationBackendKind.Collision, true);
foreach (var backend in found)
{
    using (backend)
        Console.WriteLine($"{backend.Name}: {backend.Available} {backend.Diagnostic}");
}
using var selected = new BackendSelection { Planner = "OMPL", Collision = "FCL" };
SimulationBackends.SetDefaults(selected);
```

`BackendDescriptor` 给出名称、算法、可用性、诊断与能力位；能力枚举包括 `EnumTreeIkCapability`、`EnumPlannerCapability`、`EnumCollisionCapability`。查询能力再选择依赖它的操作；`BackendUnavailable` 和 `UnsupportedCapability` 是不同失败。默认值只是新会话的选择，已有 `RobotSimulationSession` 会保存自己的解析结果。本机使用正式包时直接执行完整 `Initialize()` 会在 Physics 插件加载处卡住，因此 RapidRobot 显式加载 Plan/FCL 后端；这是已观察到的环境行为，不能推广为所有安装均如此。

## 3. URDF、机器人拓扑与状态

**读入：** `RobotIO.Load(Path)` 或 `Load(Path, RobotUrdfLoadOptions)` 返回 `RobotBody`。`RobotUrdfLoadOptions` 提供 `LengthScale`（默认按 URDF 米制长度乘 1000 进入机器人内部毫米）、`PackageRoot` 和 `CollisionFallback`。相对网格路径依赖 URDF 所在目录/包根。检查返回值、Link/Joint/Arm 数量，并读取 URDF 的碰撞诊断后再规划。

```csharp
using var file = new AnyCAD.Foundation.Path(urdfFile);
using var options = new RobotUrdfLoadOptions { LengthScale = 1000.0 };
using var robot = RobotIO.Load(file, options);
if (robot == null || robot.GetArmCount() == 0)
    throw new InvalidOperationException("URDF 没有可用的 Arm");
```

**自建：** `RobotModel` 用 `AddLink/AddJoint/AddFrame/AddArm` 建立拓扑，`SetRootLink` 指定根；`RobotLink` 存名字、父 Joint 和碰撞形状，`RobotJointEdge` 存父子 Link、关节类型、轴、原点与运动限位，`RobotFrame` 绑定 Link 与局部姿态，`RobotArm` 指定 Base Link 与 Tip Frame。`RobotBody.Create(model)` 在验证模型后建立可运动实体。四种 `Robot*Index` 不能混用；它们的 `IsValid/GetValue` 与各自集合用于安全索引。`RobotModel.GetTopologicalLinkIndices` 可检查遍历顺序。

**运动：** `RobotBody.GetJointPosition/SetJointPosition` 和按 Arm 路径偏移的 `GetArmJointPosition/SetArmJointPosition` 处理状态。改变关节后调用 `UpdateFrames()`；`GetLinkTransform/GetFrameTransform/GetArmTipTransform(..., true)` 返回世界变换，`false` 返回机器人内部坐标。`GetStateStamp` 与 `ApplyJointPositions(..., stamp)` 可拒绝过期 IK 结果；`ResetToHome/ResetArmToHome/ShowLinks/GetLinkVisualRoot` 管理状态与显示。连续关节允许超出 `2π`；有界与移动关节应遵守各自限位和内部长度单位。

`RobotData`、`RobotJointPosition`、`RobotBodyStateStamp` 和枚举 `EnumRobotJointApplyStatus` 是状态交换的数据类型。由 `RobotBody.GetModel()` 取得的模型与主体有关联，释放顺序应让使用它的会话先结束。

## 4. DH、FK 与 IK

**DH/Frame：** `RobotDH` 保存关节类型、`a/alpha/d/theta` 和 Frame；`RobotFrameBuilder.Add` 顺序加入参数，`Build(EnumDHComputeMethod)` 后用 `GetFrame/GetDH/GetJointType` 读取结果。长度按当前机器人单位、角度用弧度；DH 构帧不自动替你创建 `RobotBody`。

**独立 FK：** `FkDefinition` 由 `FkRoot/FkJoint/FkFrame` 列表描述森林，`FkEvaluator(definition).Evaluate(DoubleList)` 返回 `FkResult`，须同时检查状态/诊断和所需 Frame/Link 变换。`JointMotion` 可单独计算指定关节类型、轴和位置的运动矩阵；输入顺序由定义的 Joint 顺序决定。`RobotModel` 可用于捕获模型的 FK 定义，RapidRobot 的自测同时覆盖森林与无效输入。

**IK 两条路径：** `RobotSingleTipIkSolver.Solve(RobotSingleTipIkRequest)` 面向单末端；`IkSolver.Solve`/`SolveTree` 与 `RobotSimulationSession.SolveIk(RobotSimulationTargetList)` 面向树形或多 Tip 目标。`TreeIkRequest` 由 Joint/Target 列表、容差、迭代等字段组成；`TreeIkResult` 的 `Status/Diagnostic/Residuals/JointPositions` 是判定依据。KDL、LMA、RL 等名字必须先由 `SimulationBackends` 确认；部分后端不支持约束投影。`RobotSimulationTarget.TargetInBody` 是根 Link 坐标中的完整姿态，不能直接传世界矩阵：

```csharp
using var rootWorld = robot.GetLinkTransform(rootIndex, true);
using var inverseRoot = rootWorld.inversed();
using var targetInBody = inverseRoot.multiplied(targetWorld);
using var target = new RobotSimulationTarget {
    FrameIndex = tipFrameIndex, TargetInBody = targetInBody
};
using var targets = new RobotSimulationTargetList();
targets.Add(target);
using var session = new RobotSimulationSession(robot);
using var solved = session.SolveIk(targets);
if (!solved.IsSuccess()) Console.WriteLine($"{solved.Status}: {solved.Diagnostic}");
```

失败可能是目标不可达、奇异位置、约束不满足、输入拓扑无效或后端不可用；不要在失败时使用残留的关节目标。`RobotJoint`/`RobotAnimation` 的传统单链 API 仍在包中，附录保留全部入口，选型时区分它们与新的 `RobotSimulationSession`。

## 5. 关节规划、轨迹时标与播放

`RobotSimulationSession(robot, backends)` 捕获源状态和后端；先检查 `IsValid/GetDiagnostic`，再用 `CreatePlanningRequest(goals, motionLimits)` 构造请求。每个活动关节都需要唯一目标与正的速度、加速度限制。`RobotPlanRequest` 还包含 `Options`、`Timing`、`CollisionScene`、`CollisionBindings`、`HeldTargets` 和姿态容差。`Plan` 返回 `RobotPlanResult`；先检查 `Succeeded()`，失败时读 `Planning.Status/Diagnostic`，成功后才取 `Planning.Path`、`Trajectory` 与曲线类型。`Cancel()` 取消该会话当前工作；不复用已取消会话进行新规划。

```csharp
using var request = session.CreatePlanningRequest(goals, limits);
using (var options = request.Options) {
    options.CollisionResolution = 0.05; // 关节空间采样间隔：rad 或模型关节单位
    options.TimeoutSeconds = 10;
}
using var result = session.Plan(request);
using var planning = result.Planning;
if (!result.Succeeded())
    throw new InvalidOperationException($"{planning.Status}: {planning.Diagnostic}");
```

`JointSpacePlanningOptions` 控制碰撞分辨率、超时、随机种子、规划器范围和 shortcut；碰撞分辨率默认为 `1`，小角度运动若包含狭窄障碍应显式缩小。`JointSpacePlanningResult` 提供后端、算法、路径、状态、诊断及 `JointSpacePlanningStatistics`。`JointTrajectoryTiming.Parameterize(DoubleList2, limits, options)` 将关节路径配时；`JointTrajectorySampler.Sample(trajectory, seconds)` 对结果采样。`JointTrajectoryOptions` 的曲线有 `CubicStopToStop`、`QuinticStopToStop` 等，使用前调用 `IsValid`。轨迹点的时间为**秒**，制造仿真快照的时间则为**毫秒**。

`RobotTrajectoryPlayer.Load(robot, result)` 在源状态匹配时装载结果，随后 `Play/Pause/Seek(seconds)/Tick(deltaSeconds)/Stop`；`GetState/GetTime/GetDuration/GetDiagnostic` 用于 UI。播放失败不得继续推进；停止会恢复起点。播放会改变 `RobotBody`，其它窗口需要同步重绘。针对多线程 UI，后台仅运行求解/规划，界面控件更新回到 UI 线程。

## 6. 规划碰撞场景

规划请求中的 `PlanningCollisionSceneData.Bodies` 包含 `PlanningCollisionBody`；Body 有 Owner ID/Name/Type、世界位姿和多个 `PlanningCollisionShape`。形状类型为 Box、Sphere、Cylinder、ConvexHull 或仅适用于静态环境的 TriangleMesh。Box 用 `Size`，球用 `Radius`，柱用 `Radius/Length`，凸包/网格用 `Vertices/Indices`，`LocalTransform` 表示形状相对 Body。移动 Link/Tool 的 Body 必须在 `RobotCollisionBindingList` 中绑定到真实 Link，`LinkToBody` 给局部变换；静态 Environment 不绑定。每个 movable Body 应有唯一 Owner ID，Shape 的 Source ID 用于身份与指纹，不是 Document Element 查询键。

`PlanningCollisionIgnoredPairList` 按两个 Owner ID 忽略碰撞；只应加入本场景中存在的实体。相邻 Link 通常需要豁免，但并非自动等于“永远无碰撞”：取消豁免后同样参与检测。`IncludeWorldTransformInFingerprint` 对静态环境位姿应开启；`SemanticFingerprintSeed` 可纳入应用层拓扑版本，`ComputeFingerprint` 用于场景等价判断。`PlanningCollisionCheckResult`/`PlanningCollisionPair` 为检查结果数据类型；**本正式包没有公开的 `PlanningCollisionScene.Build` .NET 入口**，通过 `RobotSimulationSession.Plan` 在请求内构建并执行规划碰撞检查。

```csharp
using var scene = request.CollisionScene;
using var bodies = scene.Bodies;
using var bindings = request.CollisionBindings;
// 每个已确认的 Link 凸包形状加入一个 Link Body，再加入对应 Binding。
// Environment Body 直接加入 Bodies；按当前 UI 状态重建 IgnoredPairs。
```

`EnumJointSpacePlanningStatus.InvalidStart/InvalidGoal` 表示起终点状态未通过有效性检查；`BackendUnavailable` 表示后端不可用，`UnsupportedCapability` 表示后端不具备请求的能力。`Diagnostic` 可能为空，状态仍必须检查。规划器沿路径按 `CollisionResolution` 采样，不应仅凭“起点和终点无碰撞”判断路径安全。

## 7. V-HACD 凸分解

`CollisionTriangleMesh.Vertices` 是三维点列表、`Indices` 是每组三个顶点的三角面索引；先调用 `IsValid()`。`VhacdOptions` 包括 `MaxConvexHulls`、`Resolution`、允许体积误差、递归深度、每凸包最大顶点数、`ShrinkWrap` 和 `EnumVhacdFillMode`；先调用 `IsValid()`。`ConvexDecompositionSession.IsAvailable()` 反映原生能力是否可用；`Compute()` 返回 `ConvexDecompositionResult`，检查 `Succeeded()/State/Diagnostic` 再读取 `Hulls`，可用 `GetProgress()` 与 `Cancel()` 做 UI。`CollisionTriangleMesh.ComputeFingerprint()` 可用于输入去重。

```csharp
using var options = new VhacdOptions {
    MaxConvexHulls = 8, Resolution = 100000,
    MinimumVolumePercentErrorAllowed = 2.5,
    MaxRecursionDepth = 8, MaxNumVerticesPerCH = 32
};
using var work = new ConvexDecompositionSession(mesh, options);
using var output = work.Compute();
if (!output.Succeeded())
    throw new InvalidOperationException($"{output.State}: {output.Diagnostic}");
using var hulls = output.Hulls; // 每项再转成独立 ConvexHull 形状
```

参数示例是 AnyDesigner“快速”预设；默认 `ShrinkWrap=true`、`FillMode=FloodFill`。输入必须在预期的 Link 局部单位中；URDF 米与 `RobotBody` 毫米混用会形成看似成功、实际错位的碰撞体。取消或失败不应覆盖调用方此前确认的结果。

## 8. 场景节点与 Document 碰撞

这组 API 与第 6 节的**规划请求值类型**用途不同。`CollisionSceneWorld.Create(name)` 对 `SceneNode` 执行 `AddObject`、`Build`、`Collide(nodeA,nodeB)`；`Begin/End/Clear` 管理批量操作和更新时机。`CollisionDocWorld.Create(name)` 先 `Initialzie(Document,ObjectId)`（正式包方法名确实拼为 `Initialzie`），再按 `ObjectId` 添加/检测对象，另有 `RayTest`。两者依赖 Foundation 的场景或 Document，不能把 `RobotPlanRequest.CollisionScene` 直接传入。

```csharp
using var world = CollisionSceneWorld.Create("inspection");
world.AddObject(firstNode);
world.AddObject(secondNode);
world.Build();
bool contact = world.Collide(firstNode, secondNode);
```

创建失败或原生碰撞后端未准备好时须先处理空结果/异常；节点变换或几何变化后重建或使用 Begin/End 更新流程。`CollisionWorld` 是基类，只有原生指针包装构造，不要直接 `new CollisionWorld()`。

## 9. 动画和机器人动画

通用 `Animation` 管理启用、暂停、循环、播放、持续时间和复位；`RigidAnimation` 与 `RigidAnimationClip` 组合 Matrix/Rotate/Move/Material 片段，对场景节点属性做变换或材质动画。`RobotAnimation` 与 `RobotAnimationClip` 面向机器人关节序列。相关枚举包括 `EnumAnimationLoopMode` 与 `EnumAnimationPlaybackState`。这些类型与第 5 节的关节空间规划/`RobotTrajectoryPlayer` **不是同一种结果格式**；若目标是碰撞安全的机器人运动，应先规划再播放其轨迹。

这些动画类在正式包中有无参构造；附录逐类列出可调用构造和方法。`(IntPtr,bool)` 重载仅用于已有原生对象的包装，不要自行制造指针。调用 `Play(time)` 前设置片段、目标节点与持续时间，并检查布尔返回值；自定义动画回调、绘制回调由 SWIG director 代理到 C# 时，应保持回调对象存活。动画涉及 Viewer/场景节点，尚未完成独立运行验证。

```csharp
using var clip = new MatrixAnimationClip(node, startMatrix, endMatrix, 0f, 1f);
using var animation = new RigidAnimation();
animation.Add(clip);
if (!animation.Play(0.5f))
    throw new InvalidOperationException("动画片段播放失败");
```

## 10. 制造仿真：铣削与增材

`ManufacturingMotion` 描述线性/弧线等运动类型、位置、刀轴、弧心偏移、圆弧平面法线、旋转角、线/角速度、工具 ID 与空行程标记。`MillProgram` 持有 `MillStockDefinition`、`MillToolDefinitionList` 和运动列表；Stock/Tool 的几何是 Foundation `SceneNode`，各自有 ID。`AdditiveProgram` 持有 `AdditiveHeadDefinitionList` 与运动列表，也提供 `ReadGCodeFile(path)`；喷头定义包含显示节点、沉积宽/高、颜色与 ID。构造这些**数据对象**无需 RapidRobot。

`ManufacturingSimulationSession` 的控制方法是 `SetViewer`、`Play/Pause/Resume/Stop/Seek(milliseconds)`、倍率、循环、快照、`GetLastError` 和 `Close`；`ManufacturingSimulationListener` 接收状态变化、关闭与失败回调。`MillSimulationSession.Start(program)`/`Start(stockNode,program)` 与 `AdditiveSimulationSession.Start(baseNode,program)` 是各自的启动操作。`ManufacturingSimulationSnapshot` 的当前/总时间均为**毫秒**。

两个派生会话在正式包中都有无参构造，可以直接在 C# 中创建。以下示意要求调用方先建立有效的 Viewer、Stock 几何及刀具/运动数据；每一步的布尔返回值和 `GetLastError()` 都应检查。增材对应 `new AdditiveSimulationSession()`、`Start(baseNode, additiveProgram)`，时间接口使用毫秒。RapidRobot 尚未运行制造仿真；Viewer、几何与原生运行环境仍需在目标应用中实际验证。

```csharp
using var mill = new MillSimulationSession();
if (!mill.SetViewer(viewer))
    throw new InvalidOperationException(mill.GetLastError()); // 现有 Foundation Viewer
if (!mill.Start(millProgram))
    throw new InvalidOperationException(mill.GetLastError());
if (!mill.Play())
    throw new InvalidOperationException(mill.GetLastError());
using var snapshot = mill.GetSnapshot();
Console.WriteLine(snapshot.CurrentTimeMilliseconds);
mill.Close();
```

## 11. 可视化、绘图与网格工具

`Matplot.Create(title)` 支持 `AddCurve`、`AddMesh`、`AddSurface`，用 `PlotRange` 和 `PlotCurveFunctor/PlotSurfaceFunctor`（或 `PlotSurfaceFunc`）定义数据，`Build(colorMap,range)` 生成 GroupSceneNode；`GetSurface` 可取得表面几何。`ColorLookupTable` 管理值域、颜色映射、离散级数与图像/颜色缓冲，`ColorMapKeyword` 选择色带；`PaletteWidget` 可显示标题和刻度。自定义 Functor 是回调对象，传给原生端时要保留 C# 强引用。

`MeshTool.ComputeSection/CreateBoundaryEdges/CreateMeshEdges/CreateWireframe` 生成剖面与边线；`NormalCalculator.ComputeVertexNormals` 为 BufferGeometry 计算法线；`BBoxUtil.ComputeStrictBBox` 计算场景节点包围盒。它们操作 Foundation 的几何/节点，须在正确的坐标系和渲染上下文下使用；无效几何或空结果要检查。`Matplot`、颜色表和网格工具并不会自动注册为机器人碰撞体，必须显式转换到第 6 或 7 节的数据结构。

```csharp
using var table = new ColorLookupTable(0.0, 1.0);
table.SetColorNumber(256);
var midpointColor = table.GetColor(0.5);
using var plot = Matplot.Create("measurements");
// 选择 PlotRange 与曲线或曲面数据后调用 AddCurve/AddSurface，再 Build。
```

## 12. 本地 SDK 差异、校验方式与限制

本手册正文仅使用正式 `2026.9.22.1736` 程序集。`../platform` 的后续源码和 `2026.9.23.1213` **本地离线包**另有 `SimulateInstance.Initialize(): bool`、`GetInitializationDiagnostic()`，以及后续 .NET Host、插件加载、渲染和 SWIG 构建调整；这些都不能直接写进正式包示例。`dev` 才固定本地版本；其初始化配置未随 RapidRobot 输出，调用会给出失败诊断，而显式加载 Plan/FCL 后仍可运行已验证的机器人示例。完整提交时间线见 [RapidRobot 开发手册](RapidRobot-开发手册.md#9-sdk-包时间线与分支)。

附录从实际 NuGet 包 `lib/net80/AnyCAD.Simulate.NET.dll` 反射得到 **180 个公开类型**，按字母排列，包括集合/枚举器；每项列出公开构造、属性与非样板方法，并标注所属功能章节。包内 XML 是另一份交叉检查材料，主要记录 SWIG 包装说明，条目不覆盖全部公开成员；缺少 XML 条目不能说明成员不存在。省略自动的 `get_/set_` 访问器、`getCPtr/swigRelease` 和通用 `Dispose` 的逐条重复：属性已列出读写入口，所有持有原生资源的包装对象按 `IDisposable` 处理。某些类型或方法虽在程序集公开，但仍可能需要插件、许可、Viewer 或 Document；该事实已在相应章节标明。实际可运行验证范围是 RapidRobot 的 FK、V-HACD、IK、OMPL/FCL、轨迹与 Kr500；制造、部分动画、绘图及 Document 碰撞目前只做签名核对，不宣称运行通过。

## 附录：正式包公开 API 索引

以下签名是版本快照。`ctor Void .ctor(IntPtr, Boolean)` 表示 SWIG 包装构造，**不是**创建原生功能对象的推荐入口。`List`/`ListEnumerator` 是跨托管边界集合。类型的完整所在命名空间均为 `AnyCAD.Simulate`，除非签名显式标出 Foundation 类型。

### `AdditiveHeadDefinition`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Vector3 DepositColor`
- `property Double DepositHeight`
- `property Double DepositWidth`
- `property SceneNode DisplayNode`
- `property Int32 Id`

### `AdditiveHeadDefinitionList`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.AdditiveHeadDefinition])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.AdditiveHeadDefinitionList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property AdditiveHeadDefinition Item`
- `method Void Add(AnyCAD.Simulate.AdditiveHeadDefinition)`
- `method Void AddRange(AnyCAD.Simulate.AdditiveHeadDefinitionList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.AdditiveHeadDefinition[])`
- `method Void CopyTo(AnyCAD.Simulate.AdditiveHeadDefinition[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.AdditiveHeadDefinition[], Int32, Int32)`
- `method AdditiveHeadDefinitionListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.AdditiveHeadDefinitionList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.AdditiveHeadDefinition)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.AdditiveHeadDefinitionList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.AdditiveHeadDefinitionList Repeat(AnyCAD.Simulate.AdditiveHeadDefinition, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.AdditiveHeadDefinitionList)`
- `method AnyCAD.Simulate.AdditiveHeadDefinition[] ToArray()`

### `AdditiveHeadDefinitionListEnumerator`

所属章节：第 10 节。
- `ctor Void .ctor(AnyCAD.Simulate.AdditiveHeadDefinitionList)`
- `property AdditiveHeadDefinition Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `AdditiveProgram`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property AdditiveHeadDefinitionList Heads`
- `property ManufacturingMotionList Motions`
- `method Boolean ReadGCodeFile(System.String)`

### `AdditiveSimulationSession`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Boolean Start(AnyCAD.Foundation.SceneNode, AnyCAD.Simulate.AdditiveProgram)`

### `Animation`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Single GetDuration()`
- `method Boolean GetEnabled()`
- `method Boolean GetLoop()`
- `method Boolean GetPaused()`
- `method Boolean Play(Single)`
- `method Void Reset()`
- `method Void SetEnabled(Boolean)`
- `method Void SetLoop(Boolean)`
- `method Void SetPaused(Boolean)`

### `BackendDescriptor`

所属章节：第 2 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Algorithm`
- `property Boolean Available`
- `property UInt32 Capabilities`
- `property String Diagnostic`
- `property String Name`
- `property String Provider`
- `method Boolean Supports(UInt32)`

### `BackendDescriptorList`

所属章节：第 2 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.BackendDescriptor])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.BackendDescriptorList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property BackendDescriptor Item`
- `method Void Add(AnyCAD.Simulate.BackendDescriptor)`
- `method Void AddRange(AnyCAD.Simulate.BackendDescriptorList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.BackendDescriptor[])`
- `method Void CopyTo(AnyCAD.Simulate.BackendDescriptor[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.BackendDescriptor[], Int32, Int32)`
- `method BackendDescriptorListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.BackendDescriptorList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.BackendDescriptor)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.BackendDescriptorList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.BackendDescriptorList Repeat(AnyCAD.Simulate.BackendDescriptor, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.BackendDescriptorList)`
- `method AnyCAD.Simulate.BackendDescriptor[] ToArray()`

### `BackendDescriptorListEnumerator`

所属章节：第 2 节。
- `ctor Void .ctor(AnyCAD.Simulate.BackendDescriptorList)`
- `property BackendDescriptor Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `BackendSelection`

所属章节：第 2 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Collision`
- `property String Ik`
- `property String Planner`

### `BBoxUtil`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Foundation.AABox ComputeStrictBBox(AnyCAD.Foundation.SceneNode)`

### `CollisionDocWorld`

所属章节：第 8 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `method Void AddObject(AnyCAD.Foundation.ObjectId)`
- `method Void Begin()`
- `method Void Build()`
- `method Void Clear()`
- `method Boolean Collide(AnyCAD.Foundation.ObjectId, AnyCAD.Foundation.ObjectId)`
- `method AnyCAD.Simulate.CollisionDocWorld Create(System.String)`
- `method Void End()`
- `method Boolean Initialzie(AnyCAD.Foundation.Document, AnyCAD.Foundation.ObjectId)`
- `method Boolean RayTest(AnyCAD.Foundation.Vector3, AnyCAD.Foundation.Vector3)`

### `CollisionSceneWorld`

所属章节：第 8 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `method Void AddObject(AnyCAD.Foundation.SceneNode)`
- `method Void Begin()`
- `method Void Build()`
- `method Void Clear()`
- `method Boolean Collide(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.SceneNode)`
- `method AnyCAD.Simulate.CollisionSceneWorld Create(System.String)`
- `method Void End()`

### `CollisionTriangleMesh`

所属章节：第 7 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Uint32List Indices`
- `property Double3List Vertices`
- `method UInt64 ComputeFingerprint()`
- `method Boolean IsValid()`

### `CollisionTriangleMeshList`

所属章节：第 7 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.CollisionTriangleMesh])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.CollisionTriangleMeshList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property CollisionTriangleMesh Item`
- `method Void Add(AnyCAD.Simulate.CollisionTriangleMesh)`
- `method Void AddRange(AnyCAD.Simulate.CollisionTriangleMeshList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.CollisionTriangleMesh[])`
- `method Void CopyTo(AnyCAD.Simulate.CollisionTriangleMesh[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.CollisionTriangleMesh[], Int32, Int32)`
- `method CollisionTriangleMeshListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.CollisionTriangleMeshList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.CollisionTriangleMesh)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.CollisionTriangleMeshList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.CollisionTriangleMeshList Repeat(AnyCAD.Simulate.CollisionTriangleMesh, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.CollisionTriangleMeshList)`
- `method AnyCAD.Simulate.CollisionTriangleMesh[] ToArray()`

### `CollisionTriangleMeshListEnumerator`

所属章节：第 7 节。
- `ctor Void .ctor(AnyCAD.Simulate.CollisionTriangleMeshList)`
- `property CollisionTriangleMesh Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `CollisionWorld`

所属章节：第 8 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`

### `ColorLookupTable`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(Double, Double)`
- `method AnyCAD.Foundation.Float32Buffer ComputeColors(AnyCAD.Foundation.DoubleList, Double)`
- `method AnyCAD.Foundation.MemoryImage CreateImage()`
- `method AnyCAD.Foundation.Vector3 GetColor(Double, Boolean)`
- `method AnyCAD.Foundation.Vector3 GetColor(Double)`
- `method AnyCAD.Simulate.ColorMapKeyword GetColorMap()`
- `method UInt32 GetColorNumber()`
- `method AnyCAD.Foundation.Vec3List GetColorTable()`
- `method Double GetMaxValue()`
- `method Double GetMinValue()`
- `method Double GetValueRange()`
- `method Void Remap()`
- `method Void SetColorMap(AnyCAD.Simulate.ColorMapKeyword)`
- `method Void SetColorNumber(UInt32)`
- `method Void SetMaxValue(Double)`
- `method Void SetMinValue(Double)`

### `ColorMapKeyword`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Void AddItem(Single, UInt32)`
- `method AnyCAD.Simulate.ColorMapKeyword Create(AnyCAD.Simulate.EnumSystemColorMap)`
- `method UInt32 GetCount()`
- `method Void Reserve(UInt32)`

### `ConvexDecompositionResult`

所属章节：第 7 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Diagnostic`
- `property CollisionTriangleMeshList Hulls`
- `property EnumConvexDecompositionState State`
- `method Boolean Succeeded()`

### `ConvexDecompositionSession`

所属章节：第 7 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Simulate.CollisionTriangleMesh, AnyCAD.Simulate.VhacdOptions)`
- `method Void Cancel()`
- `method AnyCAD.Simulate.ConvexDecompositionResult Compute()`
- `method Double GetProgress()`
- `method AnyCAD.Simulate.EnumConvexDecompositionState GetState()`
- `method Boolean IsAvailable()`

### `EnumAnimationLoopMode`

所属章节：第 9 节。
- `Enum: Once, Loop`

### `EnumAnimationPlaybackState`

所属章节：第 9 节。
- `Enum: Stopped, Playing, Paused, Finished`

### `EnumCollisionCapability`

所属章节：第 6 节。
- `Enum: Primitives, ConvexHull, StaticTriangleMesh, OwnerPairFilter`

### `EnumConvexDecompositionState`

所属章节：第 7 节。
- `Enum: Ready, Running, Succeeded, Cancelled, InvalidInput, BackendUnavailable, Failed`

### `EnumDHComputeMethod`

所属章节：第 4 节。
- `Enum: Standard_DH, Modified_DH`

### `EnumFkStatus`

所属章节：第 4 节。
- `Enum: Success, InvalidTopology, InvalidTransform, InvalidAxis, UnsupportedJoint, InvalidPositions, NumericalFailure`

### `EnumJointSpacePlanningStatus`

所属章节：第 5 节。
- `Enum: Success, InvalidStart, InvalidGoal, Timeout, NoPath, Cancelled, BackendUnavailable, InternalError, UnsupportedCapability`

### `EnumJointSpaceVariableType`

所属章节：第 5 节。
- `Enum: Bounded, Periodic`

### `EnumJointTrajectoryCurve`

所属章节：第 5 节。
- `Enum: CubicStopToStop, QuinticStopToStop`

### `EnumManufacturingMotionType`

所属章节：第 10 节。
- `Enum: Nop, SetPose, MoveRapid, MoveLinear, MoveArcCW, MoveArcCCW, ChangeTool`

### `EnumPlannerCapability`

所属章节：第 2 节。
- `Enum: Bounded, Periodic, Projection, Cancellation, Seed, MotionValidation`

### `EnumPlanningCollisionOwnerType`

所属章节：第 6 节。
- `Enum: Link, Tool, Environment`

### `EnumPlanningCollisionShapeType`

所属章节：第 6 节。
- `Enum: Box, Sphere, Cylinder, ConvexHull, TriangleMesh`

### `EnumRobotIkStatus`

所属章节：第 4 节。
- `Enum: Success, InvalidBody, InvalidArm, InvalidTarget, InvalidSeed, InvalidOptions, UnsupportedJoint, Unreachable, IterationLimit, SolverFailure, LimitViolation, TargetNotReached, BackendUnavailable, UnsupportedCapability, Cancelled`

### `EnumRobotJointApplyStatus`

所属章节：第 3 节。
- `Enum: Success, InvalidInput, BodyMismatch, StaleState`

### `EnumRobotJointAxis`

所属章节：第 3 节。
- `Enum: X, Y, Z`

### `EnumRobotJointType`

所属章节：第 3 节。
- `Enum: Fixed, Revolute, Prismatic, Continuous`

### `EnumRobotPlaybackState`

所属章节：第 5 节。
- `Enum: Empty, Ready, Playing, Paused, Finished, Failed`

### `EnumSimulationBackendKind`

所属章节：第 2 节。
- `Enum: Ik, Planner, Collision`

### `EnumSystemColorMap`

所属章节：第 11 节。
- `Enum: Rainbow, Cooltowarm, Blackbody, Grayscale`

### `EnumTreeIkCapability`

所属章节：第 2 节。
- `Enum: Tree, SingleTipFullPose, MultiTipFullPose, JointLimits, ContinuousJoint, ConstraintProjection`

### `EnumTreeIkStatus`

所属章节：第 4 节。
- `Enum: Success, InvalidTree, InvalidTarget, InvalidSeed, InvalidOptions, UnsupportedJoint, JointLimitsRequired, NoConvergence, NumericalFailure, LimitViolation, TargetNotReached, BackendUnavailable, UnsupportedCapability, Cancelled`

### `EnumVhacdFillMode`

所属章节：第 7 节。
- `Enum: FloodFill, SurfaceOnly, RaycastFill`

### `FkDefinition`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property FkFrameList Frames`
- `property FkJointList Joints`
- `property UInt32 LinkCount`
- `property FkRootList Roots`

### `FkEvaluator`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Simulate.FkDefinition)`
- `method AnyCAD.Simulate.FkResult Evaluate(AnyCAD.Foundation.DoubleList)`
- `method Boolean IsValid()`
- `method AnyCAD.Foundation.Matrix4d JointMotion(AnyCAD.Simulate.EnumRobotJointType, AnyCAD.Foundation.Vector3d, Double)`

### `FkFrame`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt32 LinkIndex`
- `property Matrix4d Origin`

### `FkFrameList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.FkFrame])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.FkFrameList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property FkFrame Item`
- `method Void Add(AnyCAD.Simulate.FkFrame)`
- `method Void AddRange(AnyCAD.Simulate.FkFrameList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.FkFrame[])`
- `method Void CopyTo(AnyCAD.Simulate.FkFrame[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.FkFrame[], Int32, Int32)`
- `method FkFrameListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.FkFrameList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.FkFrame)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.FkFrameList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.FkFrameList Repeat(AnyCAD.Simulate.FkFrame, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.FkFrameList)`
- `method AnyCAD.Simulate.FkFrame[] ToArray()`

### `FkFrameListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.FkFrameList)`
- `property FkFrame Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `FkJoint`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Vector3d Axis`
- `property UInt32 ChildLinkIndex`
- `property EnumRobotJointType JointType`
- `property Matrix4d Origin`
- `property UInt32 ParentLinkIndex`

### `FkJointList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.FkJoint])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.FkJointList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property FkJoint Item`
- `method Void Add(AnyCAD.Simulate.FkJoint)`
- `method Void AddRange(AnyCAD.Simulate.FkJointList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.FkJoint[])`
- `method Void CopyTo(AnyCAD.Simulate.FkJoint[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.FkJoint[], Int32, Int32)`
- `method FkJointListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.FkJointList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.FkJoint)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.FkJointList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.FkJointList Repeat(AnyCAD.Simulate.FkJoint, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.FkJointList)`
- `method AnyCAD.Simulate.FkJoint[] ToArray()`

### `FkJointListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.FkJointList)`
- `property FkJoint Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `FkResult`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Diagnostic`
- `property UInt32 FrameIndex`
- `property Matrix4dList FrameTransforms`
- `property UInt32 JointIndex`
- `property Matrix4dList JointTransforms`
- `property UInt32 LinkIndex`
- `property Matrix4dList LinkTransforms`
- `property EnumFkStatus Status`
- `method Boolean Succeeded()`

### `FkRoot`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt32 LinkIndex`
- `property Matrix4d Transform`

### `FkRootList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.FkRoot])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.FkRootList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property FkRoot Item`
- `method Void Add(AnyCAD.Simulate.FkRoot)`
- `method Void AddRange(AnyCAD.Simulate.FkRootList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.FkRoot[])`
- `method Void CopyTo(AnyCAD.Simulate.FkRoot[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.FkRoot[], Int32, Int32)`
- `method FkRootListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.FkRootList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.FkRoot)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.FkRootList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.FkRootList Repeat(AnyCAD.Simulate.FkRoot, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.FkRootList)`
- `method AnyCAD.Simulate.FkRoot[] ToArray()`

### `FkRootListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.FkRootList)`
- `property FkRoot Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `IkSolver`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.IkSolver Create(System.String)`
- `method AnyCAD.Simulate.BackendDescriptor Describe()`
- `method Double GetAngleEps()`
- `method Int32 GetMaxIterations()`
- `method Double GetPositionEps()`
- `method AnyCAD.Foundation.Matrix4d GetTcp()`
- `method Double GetValue(UInt32)`
- `method Boolean MoveTo(AnyCAD.Foundation.Vector3d, AnyCAD.Foundation.Matrix3d)`
- `method Boolean MoveToFrame(AnyCAD.Foundation.Matrix4d)`
- `method Void SetAngleEps(Double)`
- `method Void SetMaxIterations(Int32)`
- `method Void SetPositionEps(Double)`
- `method AnyCAD.Simulate.TreeIkResult Solve(System.String, AnyCAD.Simulate.TreeIkRequest, Boolean)`
- `method AnyCAD.Simulate.TreeIkResult Solve(System.String, AnyCAD.Simulate.TreeIkRequest)`
- `method AnyCAD.Simulate.TreeIkResult SolveTree(AnyCAD.Simulate.TreeIkRequest, Boolean)`
- `method AnyCAD.Simulate.TreeIkResult SolveTree(AnyCAD.Simulate.TreeIkRequest)`

### `JointSpacePlanningOptions`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Double CollisionResolution`
- `property Boolean EnableShortcut`
- `property Double NormalizedPlannerRange`
- `property UInt32 Seed`
- `property UInt32 ShortcutAttempts`
- `property Double TimeoutSeconds`

### `JointSpacePlanningResult`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Algorithm`
- `property String Backend`
- `property String Diagnostic`
- `property DoubleList2 Path`
- `property JointSpacePlanningStatistics Statistics`
- `property EnumJointSpacePlanningStatus Status`
- `method Boolean Succeeded()`

### `JointSpacePlanningStatistics`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt64 CollisionCheckCount`
- `property Double ElapsedSeconds`
- `property UInt64 MotionCheckCount`
- `property UInt64 PathStateCount`
- `property UInt64 ProjectionCount`
- `property UInt64 SampleCount`
- `property UInt32 Seed`
- `property UInt64 StateCheckCount`

### `JointSpacePlanningVariable`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Double LowerBound`
- `property EnumJointSpaceVariableType Type`
- `property Double UpperBound`

### `JointTrajectory`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property EnumJointTrajectoryCurve Curve`
- `property JointTrajectoryPointList Points`
- `method Boolean IsValid()`

### `JointTrajectoryLimit`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Double MaxAcceleration`
- `property Double MaxVelocity`
- `property Double Period`

### `JointTrajectoryLimitList`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.JointTrajectoryLimit])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.JointTrajectoryLimitList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property JointTrajectoryLimit Item`
- `method Void Add(AnyCAD.Simulate.JointTrajectoryLimit)`
- `method Void AddRange(AnyCAD.Simulate.JointTrajectoryLimitList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.JointTrajectoryLimit[])`
- `method Void CopyTo(AnyCAD.Simulate.JointTrajectoryLimit[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.JointTrajectoryLimit[], Int32, Int32)`
- `method JointTrajectoryLimitListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.JointTrajectoryLimitList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.JointTrajectoryLimit)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.JointTrajectoryLimitList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.JointTrajectoryLimitList Repeat(AnyCAD.Simulate.JointTrajectoryLimit, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.JointTrajectoryLimitList)`
- `method AnyCAD.Simulate.JointTrajectoryLimit[] ToArray()`

### `JointTrajectoryLimitListEnumerator`

所属章节：第 5 节。
- `ctor Void .ctor(AnyCAD.Simulate.JointTrajectoryLimitList)`
- `property JointTrajectoryLimit Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `JointTrajectoryOptions`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property EnumJointTrajectoryCurve Curve`
- `property Double MinimumDuration`
- `property Double MinimumSegmentDuration`
- `property Double PositionTolerance`
- `property Double TimeTolerance`
- `method Boolean IsValid()`

### `JointTrajectoryPoint`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property DoubleList Positions`
- `property Double Time`

### `JointTrajectoryPointList`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.JointTrajectoryPoint])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.JointTrajectoryPointList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property JointTrajectoryPoint Item`
- `method Void Add(AnyCAD.Simulate.JointTrajectoryPoint)`
- `method Void AddRange(AnyCAD.Simulate.JointTrajectoryPointList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.JointTrajectoryPoint[])`
- `method Void CopyTo(AnyCAD.Simulate.JointTrajectoryPoint[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.JointTrajectoryPoint[], Int32, Int32)`
- `method JointTrajectoryPointListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.JointTrajectoryPointList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.JointTrajectoryPoint)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.JointTrajectoryPointList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.JointTrajectoryPointList Repeat(AnyCAD.Simulate.JointTrajectoryPoint, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.JointTrajectoryPointList)`
- `method AnyCAD.Simulate.JointTrajectoryPoint[] ToArray()`

### `JointTrajectoryPointListEnumerator`

所属章节：第 5 节。
- `ctor Void .ctor(AnyCAD.Simulate.JointTrajectoryPointList)`
- `property JointTrajectoryPoint Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `JointTrajectorySample`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Diagnostic`
- `property DoubleList Positions`
- `property Boolean Succeeded`

### `JointTrajectorySampler`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.JointTrajectorySample Sample(AnyCAD.Simulate.JointTrajectory, Double)`

### `JointTrajectoryTiming`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.JointTrajectoryTimingResult Parameterize(AnyCAD.Foundation.DoubleList2, AnyCAD.Simulate.JointTrajectoryLimitList, AnyCAD.Simulate.JointTrajectoryOptions)`
- `method AnyCAD.Simulate.JointTrajectoryTimingResult Parameterize(AnyCAD.Foundation.DoubleList2, AnyCAD.Simulate.JointTrajectoryLimitList)`

### `JointTrajectoryTimingResult`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Diagnostic`
- `property Boolean Succeeded`
- `property JointTrajectory Trajectory`

### `ManufacturingMotion`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Double AngularSpeed`
- `property Vector3d ArcCenterOffset`
- `property Vector3d ArcPlaneNormal`
- `property Boolean IsTravel`
- `property Double LinearSpeed`
- `property Vector3d Position`
- `property Double RotationAngle`
- `property Vector3d ToolAxis`
- `property Int32 ToolId`
- `property EnumManufacturingMotionType Type`

### `ManufacturingMotionList`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.ManufacturingMotion])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.ManufacturingMotionList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property ManufacturingMotion Item`
- `method Void Add(AnyCAD.Simulate.ManufacturingMotion)`
- `method Void AddRange(AnyCAD.Simulate.ManufacturingMotionList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.ManufacturingMotion[])`
- `method Void CopyTo(AnyCAD.Simulate.ManufacturingMotion[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.ManufacturingMotion[], Int32, Int32)`
- `method ManufacturingMotionListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.ManufacturingMotionList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.ManufacturingMotion)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.ManufacturingMotionList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.ManufacturingMotionList Repeat(AnyCAD.Simulate.ManufacturingMotion, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.ManufacturingMotionList)`
- `method AnyCAD.Simulate.ManufacturingMotion[] ToArray()`

### `ManufacturingMotionListEnumerator`

所属章节：第 10 节。
- `ctor Void .ctor(AnyCAD.Simulate.ManufacturingMotionList)`
- `property ManufacturingMotion Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `ManufacturingSimulationListener`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Void OnSimulationChanged(AnyCAD.Simulate.ManufacturingSimulationSnapshot)`
- `method Void OnSimulationClosed()`
- `method Void OnSimulationFailed(System.String)`

### `ManufacturingSimulationSession`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Boolean Close()`
- `method System.String GetLastError()`
- `method AnyCAD.Simulate.ManufacturingSimulationSnapshot GetSnapshot()`
- `method AnyCAD.Foundation.Viewer GetViewer()`
- `method Boolean IsActive()`
- `method Boolean Pause()`
- `method Boolean Play()`
- `method Boolean Resume()`
- `method Boolean Seek(Double)`
- `method Void SetListener(AnyCAD.Simulate.ManufacturingSimulationListener)`
- `method Boolean SetLoopMode(AnyCAD.Simulate.EnumAnimationLoopMode)`
- `method Boolean SetPlaybackRate(Double)`
- `method Boolean SetViewer(AnyCAD.Foundation.Viewer)`
- `method Boolean Stop()`

### `ManufacturingSimulationSnapshot`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Boolean Active`
- `property Double CurrentTimeMilliseconds`
- `property Double DurationMilliseconds`
- `property EnumAnimationLoopMode LoopMode`
- `property Double PlaybackRate`
- `property EnumAnimationPlaybackState PlaybackState`

### `MaterialAnimationClip`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.MaterialInstance, AnyCAD.Foundation.MaterialInstance, Single, Single, Single)`
- `method Single GetDuration()`
- `method AnyCAD.Foundation.MaterialInstance GetEdgeMaterial()`
- `method AnyCAD.Foundation.MaterialInstance GetFaceMaterial()`
- `method AnyCAD.Foundation.SceneNode GetNode()`
- `method Boolean Play(Single)`
- `method Void Reset()`
- `method Void SetDuration(Single)`
- `method Void SetEdgeMaterial(AnyCAD.Foundation.MaterialInstance)`
- `method Void SetFaceMaterial(AnyCAD.Foundation.MaterialInstance)`
- `method Void SetNode(AnyCAD.Foundation.SceneNode)`

### `Matplot`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.String)`
- `method Boolean AddCurve(AnyCAD.Simulate.PlotRange, AnyCAD.Simulate.PlotCurveFunctor, AnyCAD.Foundation.MaterialInstance)`
- `method Boolean AddMesh(UInt32, UInt32, AnyCAD.Simulate.PlotSurfaceFunctor, Boolean)`
- `method Boolean AddMesh(UInt32, UInt32, AnyCAD.Simulate.PlotSurfaceFunc, Boolean)`
- `method Boolean AddSurface(AnyCAD.Simulate.PlotRange, AnyCAD.Simulate.PlotRange, AnyCAD.Simulate.PlotSurfaceFunctor, Boolean)`
- `method Boolean AddSurface(AnyCAD.Simulate.PlotRange, AnyCAD.Simulate.PlotRange, AnyCAD.Simulate.PlotSurfaceFunc, Boolean)`
- `method AnyCAD.Foundation.GroupSceneNode Build(AnyCAD.Simulate.ColorMapKeyword, AnyCAD.Simulate.PlotRange)`
- `method AnyCAD.Foundation.GroupSceneNode Build(AnyCAD.Simulate.ColorMapKeyword)`
- `method AnyCAD.Simulate.Matplot Create(System.String)`
- `method AnyCAD.Simulate.ColorLookupTable GetColorTable()`
- `method AnyCAD.Foundation.MaterialInstance GetMaterial()`
- `method Boolean GetShowLine()`
- `method AnyCAD.Foundation.BufferGeometry GetSurface(UInt32)`
- `method System.String GetTitle()`
- `method Void SetShowLine(Boolean)`
- `method Void SetTitle(System.String)`
- `method Boolean Show(UInt32, UInt32)`

### `MatrixAnimationClip`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.Matrix4d, AnyCAD.Foundation.Matrix4d, Single, Single)`
- `method AnyCAD.Foundation.Matrix4d GetEndMatrix()`
- `method AnyCAD.Foundation.SceneNode GetNode()`
- `method AnyCAD.Foundation.Matrix4d GetStartMatrix()`
- `method Boolean Play(Single)`
- `method Void SetEndMatrix(AnyCAD.Foundation.Matrix4d)`
- `method Void SetNode(AnyCAD.Foundation.SceneNode)`
- `method Void SetStartMatrix(AnyCAD.Foundation.Matrix4d)`

### `MeshTool`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Foundation.BufferGeometry ComputeSection(AnyCAD.Foundation.BufferGeometry, AnyCAD.Foundation.Plane, Single, Single)`
- `method AnyCAD.Foundation.BufferGeometry ComputeSection(AnyCAD.Foundation.BufferGeometry, Single, Single, Single)`
- `method AnyCAD.Foundation.BufferGeometry CreateBoundaryEdges(AnyCAD.Foundation.BufferGeometry)`
- `method AnyCAD.Foundation.BufferGeometry CreateMeshEdges(AnyCAD.Foundation.BufferGeometry, Single)`
- `method AnyCAD.Foundation.BufferGeometry CreateWireframe(AnyCAD.Foundation.BufferGeometry)`

### `MillProgram`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property ManufacturingMotionList Motions`
- `property MillStockDefinition Stock`
- `property MillToolDefinitionList Tools`

### `MillSimulationSession`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Boolean Start(AnyCAD.Simulate.MillProgram)`
- `method Boolean Start(AnyCAD.Foundation.SceneNode, AnyCAD.Simulate.MillProgram)`

### `MillStockDefinition`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Int32 Id`
- `property SceneNode StockNode`

### `MillToolDefinition`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Vector3 CutColor`
- `property Int32 Id`
- `property SceneNode ToolNode`

### `MillToolDefinitionList`

所属章节：第 10 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.MillToolDefinition])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.MillToolDefinitionList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property MillToolDefinition Item`
- `method Void Add(AnyCAD.Simulate.MillToolDefinition)`
- `method Void AddRange(AnyCAD.Simulate.MillToolDefinitionList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.MillToolDefinition[])`
- `method Void CopyTo(AnyCAD.Simulate.MillToolDefinition[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.MillToolDefinition[], Int32, Int32)`
- `method MillToolDefinitionListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.MillToolDefinitionList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.MillToolDefinition)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.MillToolDefinitionList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.MillToolDefinitionList Repeat(AnyCAD.Simulate.MillToolDefinition, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.MillToolDefinitionList)`
- `method AnyCAD.Simulate.MillToolDefinition[] ToArray()`

### `MillToolDefinitionListEnumerator`

所属章节：第 10 节。
- `ctor Void .ctor(AnyCAD.Simulate.MillToolDefinitionList)`
- `property MillToolDefinition Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `MoveAnimationClip`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.Vector3d, Single, Single)`
- `ctor Void .ctor(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.Vector3d, Double, Single, Single)`
- `method AnyCAD.Foundation.Vector3d GetDirection()`
- `method Double GetDistance()`
- `method AnyCAD.Foundation.SceneNode GetNode()`
- `method Boolean Play(Single)`
- `method Void Reset()`
- `method Void SetDirection(AnyCAD.Foundation.Vector3d)`
- `method Void SetDistance(Double)`
- `method Void SetNode(AnyCAD.Foundation.SceneNode)`

### `NormalCalculator`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Boolean ComputeVertexNormals(AnyCAD.Foundation.BufferGeometry)`

### `PaletteWidget`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Void AddItemText(System.String)`
- `method AnyCAD.Simulate.PaletteWidget Cast(AnyCAD.Foundation.GfxObject)`
- `method Void ClearItems()`
- `method Single GetFontSize()`
- `method Single GetHeight()`
- `method UInt32 GetTextCount()`
- `method Single GetWidth()`
- `method Void SetFontSize(Single)`
- `method Void SetHeight(Single)`
- `method Void SetTextColor(AnyCAD.Foundation.Vector3)`
- `method Void SetTextCount(UInt32)`
- `method Void SetTitle(System.String)`
- `method Void SetWidth(Single)`
- `method Boolean Update(AnyCAD.Simulate.ColorLookupTable)`

### `PlanningCollisionBody`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property ObjectId AttachmentId`
- `property Boolean IncludeWorldTransformInFingerprint`
- `property ObjectId OwnerId`
- `property String OwnerName`
- `property EnumPlanningCollisionOwnerType OwnerType`
- `property PlanningCollisionShapeList Shapes`
- `property GTrsf WorldTransform`

### `PlanningCollisionBodyList`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.PlanningCollisionBody])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionBodyList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property PlanningCollisionBody Item`
- `method Void Add(AnyCAD.Simulate.PlanningCollisionBody)`
- `method Void AddRange(AnyCAD.Simulate.PlanningCollisionBodyList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionBody[])`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionBody[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.PlanningCollisionBody[], Int32, Int32)`
- `method PlanningCollisionBodyListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.PlanningCollisionBodyList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.PlanningCollisionBody)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.PlanningCollisionBodyList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.PlanningCollisionBodyList Repeat(AnyCAD.Simulate.PlanningCollisionBody, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.PlanningCollisionBodyList)`
- `method AnyCAD.Simulate.PlanningCollisionBody[] ToArray()`

### `PlanningCollisionBodyListEnumerator`

所属章节：第 6 节。
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionBodyList)`
- `property PlanningCollisionBody Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `PlanningCollisionCheckResult`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Diagnostic`
- `property Boolean InCollision`
- `property PlanningCollisionPairList Pairs`
- `property Boolean Succeeded`

### `PlanningCollisionIgnoredPair`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property ObjectId FirstId`
- `property ObjectId SecondId`

### `PlanningCollisionIgnoredPairList`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.PlanningCollisionIgnoredPair])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionIgnoredPairList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property PlanningCollisionIgnoredPair Item`
- `method Void Add(AnyCAD.Simulate.PlanningCollisionIgnoredPair)`
- `method Void AddRange(AnyCAD.Simulate.PlanningCollisionIgnoredPairList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionIgnoredPair[])`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionIgnoredPair[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.PlanningCollisionIgnoredPair[], Int32, Int32)`
- `method PlanningCollisionIgnoredPairListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.PlanningCollisionIgnoredPairList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.PlanningCollisionIgnoredPair)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.PlanningCollisionIgnoredPairList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.PlanningCollisionIgnoredPairList Repeat(AnyCAD.Simulate.PlanningCollisionIgnoredPair, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.PlanningCollisionIgnoredPairList)`
- `method AnyCAD.Simulate.PlanningCollisionIgnoredPair[] ToArray()`

### `PlanningCollisionIgnoredPairListEnumerator`

所属章节：第 6 节。
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionIgnoredPairList)`
- `property PlanningCollisionIgnoredPair Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `PlanningCollisionPair`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property ObjectId FirstId`
- `property String FirstName`
- `property ObjectId SecondId`
- `property String SecondName`

### `PlanningCollisionPairList`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.PlanningCollisionPair])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionPairList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property PlanningCollisionPair Item`
- `method Void Add(AnyCAD.Simulate.PlanningCollisionPair)`
- `method Void AddRange(AnyCAD.Simulate.PlanningCollisionPairList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionPair[])`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionPair[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.PlanningCollisionPair[], Int32, Int32)`
- `method PlanningCollisionPairListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.PlanningCollisionPairList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.PlanningCollisionPair)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.PlanningCollisionPairList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.PlanningCollisionPairList Repeat(AnyCAD.Simulate.PlanningCollisionPair, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.PlanningCollisionPairList)`
- `method AnyCAD.Simulate.PlanningCollisionPair[] ToArray()`

### `PlanningCollisionPairListEnumerator`

所属章节：第 6 节。
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionPairList)`
- `property PlanningCollisionPair Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `PlanningCollisionSceneData`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property PlanningCollisionBodyList Bodies`
- `property PlanningCollisionIgnoredPairList IgnoredPairs`
- `property GTrsf KinematicWorldTransform`
- `property UInt64 SemanticFingerprintSeed`
- `method AnyCAD.Simulate.PlanningWorldFingerprint ComputeFingerprint()`

### `PlanningCollisionShape`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Uint32List Indices`
- `property Double Length`
- `property GTrsf LocalTransform`
- `property Double Radius`
- `property Vector3d Size`
- `property ObjectId SourceId`
- `property EnumPlanningCollisionShapeType Type`
- `property Double3List Vertices`

### `PlanningCollisionShapeList`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.PlanningCollisionShape])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionShapeList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property PlanningCollisionShape Item`
- `method Void Add(AnyCAD.Simulate.PlanningCollisionShape)`
- `method Void AddRange(AnyCAD.Simulate.PlanningCollisionShapeList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionShape[])`
- `method Void CopyTo(AnyCAD.Simulate.PlanningCollisionShape[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.PlanningCollisionShape[], Int32, Int32)`
- `method PlanningCollisionShapeListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.PlanningCollisionShapeList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.PlanningCollisionShape)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.PlanningCollisionShapeList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.PlanningCollisionShapeList Repeat(AnyCAD.Simulate.PlanningCollisionShape, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.PlanningCollisionShapeList)`
- `method AnyCAD.Simulate.PlanningCollisionShape[] ToArray()`

### `PlanningCollisionShapeListEnumerator`

所属章节：第 6 节。
- `ctor Void .ctor(AnyCAD.Simulate.PlanningCollisionShapeList)`
- `property PlanningCollisionShape Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `PlanningWorldFingerprint`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt64 Value`

### `PlotCurveFunctor`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Foundation.Vector3 f(UInt32, Single)`

### `PlotRange`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(Single, Single, Single)`
- `method Boolean Contains(Single)`
- `method Single GetMax()`
- `method Single GetMin()`
- `method Single GetRange()`
- `method Single GetStep()`
- `method UInt32 GetSteps()`
- `method Boolean IsValid()`
- `method Void SetMax(Single)`
- `method Void SetMin(Single)`
- `method Void SetStep(Single)`

### `PlotSurfaceFunc`

所属章节：第 11 节。
- `ctor Void .ctor(System.Object, IntPtr)`
- `method System.IAsyncResult BeginInvoke(UInt32, UInt32, Double, Double, System.AsyncCallback, System.Object)`
- `method AnyCAD.Foundation.GPnt EndInvoke(System.IAsyncResult)`
- `method AnyCAD.Foundation.GPnt Invoke(UInt32, UInt32, Double, Double)`

### `PlotSurfaceFunctor`

所属章节：第 11 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Foundation.Vector3 f(UInt32, UInt32, Single, Single)`

### `RigidAnimation`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Void Add(AnyCAD.Simulate.RigidAnimationClip)`
- `method System.String GetName()`
- `method Boolean Play(Single)`
- `method Void Reset()`
- `method Void SetName(System.String)`

### `RigidAnimationClip`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(Single, Single)`
- `method Boolean GetEnabled()`
- `method Single GetEndTime()`
- `method Single GetStartTime()`
- `method Boolean Play(Single)`
- `method Void Reset()`
- `method Void SetEnabled(Boolean)`
- `method Void SetEndTime(Single)`
- `method Void SetStartTime(Single)`

### `RobotAnimation`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotBody, AnyCAD.Simulate.RobotArmIndex)`
- `method Void AddClip(AnyCAD.Simulate.RobotAnimationClip)`
- `method AnyCAD.Simulate.RobotArmIndex GetArmIndex()`
- `method AnyCAD.Simulate.RobotBody GetBody()`
- `method AnyCAD.Simulate.RobotAnimationClip GetClip(UInt32, UInt32)`
- `method UInt32 GetClipCountOfJoint(UInt32)`
- `method System.String GetName()`
- `method Boolean Play(Single)`
- `method Boolean RemoveClip(AnyCAD.Simulate.RobotAnimationClip)`
- `method Void Reset()`
- `method Void SetArmIndex(AnyCAD.Simulate.RobotArmIndex)`
- `method Void SetBody(AnyCAD.Simulate.RobotBody)`
- `method Void SetName(System.String)`

### `RobotAnimationClip`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(UInt32, Double, Double, Double, Double)`
- `method Double ComputeSpeed()`
- `method Boolean GetEnabled()`
- `method Double GetEndTime()`
- `method Double GetEndVar()`
- `method UInt32 GetPathJointOffset()`
- `method Double GetStartTime()`
- `method Double GetStartVar()`
- `method Boolean Play(AnyCAD.Simulate.RobotBody, AnyCAD.Simulate.RobotArmIndex, Double)`
- `method Void SetEnabled(Boolean)`
- `method Void SetEndTime(Double)`
- `method Void SetEndVar(Double)`
- `method Void SetPathJointOffset(UInt32)`
- `method Void SetStartTime(Double)`
- `method Void SetStartVar(Double)`

### `RobotArm`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.RobotLinkIndex GetBaseLinkIndex()`
- `method UInt32 GetJointCount()`
- `method AnyCAD.Simulate.RobotJointIndexList GetJointIndices()`
- `method System.String GetName()`
- `method AnyCAD.Simulate.RobotFrameIndex GetTipFrameIndex()`
- `method Void SetBaseLinkIndex(AnyCAD.Simulate.RobotLinkIndex)`
- `method Void SetName(System.String)`
- `method Void SetTipFrameIndex(AnyCAD.Simulate.RobotFrameIndex)`

### `RobotArmIndex`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(UInt32)`
- `method UInt32 GetValue()`
- `method Boolean IsValid()`

### `RobotArmIndexList`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotArmIndex])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotArmIndexList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotArmIndex Item`
- `method Void Add(AnyCAD.Simulate.RobotArmIndex)`
- `method Void AddRange(AnyCAD.Simulate.RobotArmIndexList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotArmIndex[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotArmIndex[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotArmIndex[], Int32, Int32)`
- `method RobotArmIndexListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotArmIndexList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotArmIndex)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotArmIndexList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotArmIndexList Repeat(AnyCAD.Simulate.RobotArmIndex, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotArmIndexList)`
- `method AnyCAD.Simulate.RobotArmIndex[] ToArray()`

### `RobotArmIndexListEnumerator`

所属章节：第 3 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotArmIndexList)`
- `property RobotArmIndex Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotBody`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.EnumRobotJointApplyStatus ApplyJointPositions(AnyCAD.Simulate.RobotJointPositionList, AnyCAD.Simulate.RobotBodyStateStamp)`
- `method AnyCAD.Simulate.EnumRobotJointApplyStatus ApplyJointPositions(AnyCAD.Simulate.RobotJointPositionList)`
- `method AnyCAD.Simulate.RobotBody Cast(AnyCAD.Foundation.GfxObject)`
- `method AnyCAD.Simulate.RobotBody Create(AnyCAD.Simulate.RobotModel)`
- `method AnyCAD.Simulate.RobotArm GetArm(AnyCAD.Simulate.RobotArmIndex)`
- `method UInt32 GetArmCount()`
- `method AnyCAD.Simulate.RobotJointIndex GetArmJointIndex(AnyCAD.Simulate.RobotArmIndex, UInt32)`
- `method Double GetArmJointPosition(AnyCAD.Simulate.RobotArmIndex, UInt32)`
- `method AnyCAD.Foundation.Matrix4d GetArmTipTransform(AnyCAD.Simulate.RobotArmIndex, Boolean)`
- `method AnyCAD.Simulate.RobotData GetData()`
- `method AnyCAD.Simulate.RobotFrame GetFrame(AnyCAD.Simulate.RobotFrameIndex)`
- `method UInt32 GetFrameCount()`
- `method AnyCAD.Foundation.Matrix4d GetFrameTransform(AnyCAD.Simulate.RobotFrameIndex, Boolean)`
- `method AnyCAD.Foundation.Matrix4d GetFrameTransform(AnyCAD.Simulate.RobotFrameIndex)`
- `method UInt64 GetInstanceId()`
- `method AnyCAD.Simulate.RobotJointEdge GetJoint(AnyCAD.Simulate.RobotJointIndex)`
- `method UInt32 GetJointCount()`
- `method Double GetJointPosition(AnyCAD.Simulate.RobotJointIndex)`
- `method AnyCAD.Simulate.RobotLink GetLink(AnyCAD.Simulate.RobotLinkIndex)`
- `method UInt32 GetLinkCount()`
- `method AnyCAD.Foundation.Matrix4d GetLinkTransform(AnyCAD.Simulate.RobotLinkIndex)`
- `method AnyCAD.Foundation.Matrix4d GetLinkTransform(AnyCAD.Simulate.RobotLinkIndex, Boolean)`
- `method AnyCAD.Foundation.GroupSceneNode GetLinkVisualRoot(AnyCAD.Simulate.RobotLinkIndex)`
- `method AnyCAD.Simulate.RobotModel GetModel()`
- `method UInt64 GetStateRevision()`
- `method AnyCAD.Simulate.RobotBodyStateStamp GetStateStamp()`
- `method Void Reset()`
- `method Boolean ResetArmToHome(AnyCAD.Simulate.RobotArmIndex)`
- `method Void ResetToHome()`
- `method Boolean SetArmJointPosition(AnyCAD.Simulate.RobotArmIndex, UInt32, Double)`
- `method Boolean SetJointPosition(AnyCAD.Simulate.RobotJointIndex, Double)`
- `method Void ShowLinks(Boolean)`
- `method Void UpdateFrames()`

### `RobotBodyStateStamp`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt64 InstanceId`
- `property UInt64 Revision`

### `RobotCollisionBinding`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotLinkIndex LinkIndex`
- `property Matrix4d LinkToBody`
- `property ObjectId OwnerId`

### `RobotCollisionBindingList`

所属章节：第 6 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotCollisionBinding])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotCollisionBindingList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotCollisionBinding Item`
- `method Void Add(AnyCAD.Simulate.RobotCollisionBinding)`
- `method Void AddRange(AnyCAD.Simulate.RobotCollisionBindingList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotCollisionBinding[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotCollisionBinding[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotCollisionBinding[], Int32, Int32)`
- `method RobotCollisionBindingListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotCollisionBindingList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotCollisionBinding)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotCollisionBindingList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotCollisionBindingList Repeat(AnyCAD.Simulate.RobotCollisionBinding, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotCollisionBindingList)`
- `method AnyCAD.Simulate.RobotCollisionBinding[] ToArray()`

### `RobotCollisionBindingListEnumerator`

所属章节：第 6 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotCollisionBindingList)`
- `property RobotCollisionBinding Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotData`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Double GetJointPosition(AnyCAD.Simulate.RobotJointIndex)`
- `method AnyCAD.Foundation.Matrix4d GetLinkTransform(AnyCAD.Simulate.RobotLinkIndex)`
- `method UInt64 GetStateRevision()`

### `RobotDH`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.EnumRobotJointType, Double, Double, Double, Double)`
- `method Double GetA()`
- `method Double GetAlpha()`
- `method Double GetD()`
- `method AnyCAD.Foundation.Matrix4d GetFrame()`
- `method AnyCAD.Simulate.EnumRobotJointType GetJointType()`
- `method Double GetTheta()`
- `method Void SetA(Double)`
- `method Void SetAlpha(Double)`
- `method Void SetD(Double)`
- `method Void SetFrame(AnyCAD.Foundation.Matrix4d)`
- `method Void SetJointType(AnyCAD.Simulate.EnumRobotJointType)`
- `method Void SetTheta(Double)`

### `RobotFrame`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.RobotLinkIndex GetLinkIndex()`
- `method System.String GetName()`
- `method AnyCAD.Foundation.Matrix4d GetOrigin()`
- `method Void SetLinkIndex(AnyCAD.Simulate.RobotLinkIndex)`
- `method Void SetName(System.String)`
- `method Void SetOrigin(AnyCAD.Foundation.Matrix4d)`

### `RobotFrameBuilder`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method Void Add(AnyCAD.Simulate.EnumRobotJointType, Double, Double, Double, Double)`
- `method Void Build(AnyCAD.Simulate.EnumDHComputeMethod)`
- `method UInt32 GetCount()`
- `method AnyCAD.Simulate.RobotDH GetDH(UInt32)`
- `method AnyCAD.Foundation.Matrix4d GetFrame(UInt32)`
- `method AnyCAD.Simulate.EnumRobotJointType GetJointType(UInt32)`

### `RobotFrameIndex`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(UInt32)`
- `method UInt32 GetValue()`
- `method Boolean IsValid()`

### `RobotFrameIndexList`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotFrameIndex])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotFrameIndexList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotFrameIndex Item`
- `method Void Add(AnyCAD.Simulate.RobotFrameIndex)`
- `method Void AddRange(AnyCAD.Simulate.RobotFrameIndexList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotFrameIndex[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotFrameIndex[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotFrameIndex[], Int32, Int32)`
- `method RobotFrameIndexListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotFrameIndexList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotFrameIndex)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotFrameIndexList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotFrameIndexList Repeat(AnyCAD.Simulate.RobotFrameIndex, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotFrameIndexList)`
- `method AnyCAD.Simulate.RobotFrameIndex[] ToArray()`

### `RobotFrameIndexListEnumerator`

所属章节：第 3 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotFrameIndexList)`
- `property RobotFrameIndex Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotIO`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.RobotBody Load(AnyCAD.Foundation.Path)`
- `method AnyCAD.Simulate.RobotBody Load(AnyCAD.Foundation.Path, AnyCAD.Simulate.RobotUrdfLoadOptions)`

### `RobotJoint`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(UInt32, AnyCAD.Simulate.EnumRobotJointType, AnyCAD.Foundation.Matrix4d, Single)`
- `method AnyCAD.Simulate.RobotJoint Clone()`
- `method AnyCAD.Foundation.AxWidget GetAxisNode()`
- `method AnyCAD.Foundation.Matrix4d GetDHFrame()`
- `method AnyCAD.Foundation.Matrix4d GetFrame()`
- `method UInt32 GetIndex()`
- `method AnyCAD.Simulate.EnumRobotJointAxis GetJointAxis()`
- `method AnyCAD.Simulate.EnumRobotJointType GetJointType()`
- `method Double GetMaxValue()`
- `method Double GetMinValue()`
- `method AnyCAD.Foundation.Matrix4d GetSystemDHFrameInv()`
- `method Double GetValue()`
- `method Double GetVelocity()`
- `method AnyCAD.Foundation.Matrix4d GetWorldFrame()`
- `method Void SetAxisNode(AnyCAD.Foundation.AxWidget)`
- `method Void SetAxisVisible(Boolean)`
- `method Void SetDHFrame(AnyCAD.Foundation.Matrix4d)`
- `method Void SetIndex(UInt32)`
- `method Void SetJointAxis(AnyCAD.Simulate.EnumRobotJointAxis)`
- `method Void SetJointType(AnyCAD.Simulate.EnumRobotJointType)`
- `method Void SetMaxValue(Double)`
- `method Void SetMinValue(Double)`
- `method Void SetSystemDHFrameInv(AnyCAD.Foundation.Matrix4d)`
- `method Void SetValue(Double)`
- `method Void SetVelocity(Double)`
- `method Void SetWorldFrame(AnyCAD.Foundation.Matrix4d)`
- `method Void Update()`

### `RobotJointEdge`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Foundation.Vector3d GetAxis()`
- `method AnyCAD.Simulate.RobotLinkIndex GetChildLinkIndex()`
- `method Double GetHome()`
- `method Double GetInitialPosition()`
- `method AnyCAD.Simulate.EnumRobotJointType GetJointType()`
- `method Double GetMaxValue()`
- `method Double GetMaxVelocity()`
- `method Double GetMinValue()`
- `method System.String GetName()`
- `method AnyCAD.Foundation.Matrix4d GetOrigin()`
- `method AnyCAD.Simulate.RobotLinkIndex GetParentLinkIndex()`
- `method Void SetAxis(AnyCAD.Foundation.Vector3d)`
- `method Void SetChildLinkIndex(AnyCAD.Simulate.RobotLinkIndex)`
- `method Void SetHome(Double)`
- `method Void SetInitialPosition(Double)`
- `method Void SetJointType(AnyCAD.Simulate.EnumRobotJointType)`
- `method Void SetMaxValue(Double)`
- `method Void SetMaxVelocity(Double)`
- `method Void SetMinValue(Double)`
- `method Void SetName(System.String)`
- `method Void SetOrigin(AnyCAD.Foundation.Matrix4d)`
- `method Void SetParentLinkIndex(AnyCAD.Simulate.RobotLinkIndex)`

### `RobotJointIndex`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(UInt32)`
- `method UInt32 GetValue()`
- `method Boolean IsValid()`

### `RobotJointIndexList`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotJointIndex])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotJointIndexList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotJointIndex Item`
- `method Void Add(AnyCAD.Simulate.RobotJointIndex)`
- `method Void AddRange(AnyCAD.Simulate.RobotJointIndexList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotJointIndex[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotJointIndex[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotJointIndex[], Int32, Int32)`
- `method RobotJointIndexListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotJointIndexList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotJointIndex)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotJointIndexList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotJointIndexList Repeat(AnyCAD.Simulate.RobotJointIndex, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotJointIndexList)`
- `method AnyCAD.Simulate.RobotJointIndex[] ToArray()`

### `RobotJointIndexListEnumerator`

所属章节：第 3 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotJointIndexList)`
- `property RobotJointIndex Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotJointPosition`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotJointIndex JointIndex`
- `property Double Position`

### `RobotJointPositionList`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotJointPosition])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotJointPositionList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotJointPosition Item`
- `method Void Add(AnyCAD.Simulate.RobotJointPosition)`
- `method Void AddRange(AnyCAD.Simulate.RobotJointPositionList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotJointPosition[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotJointPosition[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotJointPosition[], Int32, Int32)`
- `method RobotJointPositionListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotJointPositionList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotJointPosition)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotJointPositionList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotJointPositionList Repeat(AnyCAD.Simulate.RobotJointPosition, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotJointPositionList)`
- `method AnyCAD.Simulate.RobotJointPosition[] ToArray()`

### `RobotJointPositionListEnumerator`

所属章节：第 3 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotJointPositionList)`
- `property RobotJointPosition Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotLink`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.RobotJointIndexList GetChildJointIndices()`
- `method System.String GetCollisionDiagnostic()`
- `method AnyCAD.Simulate.PlanningCollisionShapeList GetCollisionShapes()`
- `method AnyCAD.Simulate.RobotFrameIndexList GetFrameIndices()`
- `method System.String GetName()`
- `method AnyCAD.Simulate.RobotJointIndex GetParentJointIndex()`
- `method AnyCAD.Foundation.SceneNodeList GetVisuals()`
- `method Void SetCollisionDiagnostic(System.String)`
- `method Void SetCollisionShapes(AnyCAD.Simulate.PlanningCollisionShapeList)`
- `method Void SetName(System.String)`
- `method Void SetVisuals(AnyCAD.Foundation.SceneNodeList)`

### `RobotLinkIndex`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(UInt32)`
- `method UInt32 GetValue()`
- `method Boolean IsValid()`

### `RobotLinkIndexList`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotLinkIndex])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotLinkIndexList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotLinkIndex Item`
- `method Void Add(AnyCAD.Simulate.RobotLinkIndex)`
- `method Void AddRange(AnyCAD.Simulate.RobotLinkIndexList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotLinkIndex[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotLinkIndex[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotLinkIndex[], Int32, Int32)`
- `method RobotLinkIndexListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotLinkIndexList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotLinkIndex)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotLinkIndexList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotLinkIndexList Repeat(AnyCAD.Simulate.RobotLinkIndex, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotLinkIndexList)`
- `method AnyCAD.Simulate.RobotLinkIndex[] ToArray()`

### `RobotLinkIndexListEnumerator`

所属章节：第 3 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotLinkIndexList)`
- `property RobotLinkIndex Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotModel`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotModel)`
- `method AnyCAD.Simulate.RobotArmIndex AddArm(AnyCAD.Simulate.RobotArm)`
- `method AnyCAD.Simulate.RobotFrameIndex AddFrame(AnyCAD.Simulate.RobotFrame)`
- `method AnyCAD.Simulate.RobotJointIndex AddJoint(AnyCAD.Simulate.RobotJointEdge)`
- `method AnyCAD.Simulate.RobotLinkIndex AddLink(AnyCAD.Simulate.RobotLink)`
- `method AnyCAD.Simulate.RobotArm GetArm(AnyCAD.Simulate.RobotArmIndex)`
- `method UInt32 GetArmCount()`
- `method AnyCAD.Simulate.RobotFrame GetFrame(AnyCAD.Simulate.RobotFrameIndex)`
- `method UInt32 GetFrameCount()`
- `method AnyCAD.Simulate.RobotJointEdge GetJoint(AnyCAD.Simulate.RobotJointIndex)`
- `method UInt32 GetJointCount()`
- `method AnyCAD.Simulate.RobotLink GetLink(AnyCAD.Simulate.RobotLinkIndex)`
- `method UInt32 GetLinkCount()`
- `method System.String GetName()`
- `method AnyCAD.Simulate.RobotLinkIndex GetRootLinkIndex()`
- `method AnyCAD.Simulate.RobotLinkIndexList GetTopologicalLinkIndices()`
- `method Boolean IsRigidTransform(AnyCAD.Foundation.Matrix4d)`
- `method Void SetName(System.String)`
- `method Void SetRootLink(AnyCAD.Simulate.RobotLinkIndex)`

### `RobotMotionLimit`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotJointIndex JointIndex`
- `property Double MaxAcceleration`
- `property Double MaxVelocity`

### `RobotMotionLimitList`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotMotionLimit])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotMotionLimitList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotMotionLimit Item`
- `method Void Add(AnyCAD.Simulate.RobotMotionLimit)`
- `method Void AddRange(AnyCAD.Simulate.RobotMotionLimitList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotMotionLimit[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotMotionLimit[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotMotionLimit[], Int32, Int32)`
- `method RobotMotionLimitListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotMotionLimitList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotMotionLimit)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotMotionLimitList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotMotionLimitList Repeat(AnyCAD.Simulate.RobotMotionLimit, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotMotionLimitList)`
- `method AnyCAD.Simulate.RobotMotionLimit[] ToArray()`

### `RobotMotionLimitListEnumerator`

所属章节：第 5 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotMotionLimitList)`
- `property RobotMotionLimit Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotPlanRequest`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotCollisionBindingList CollisionBindings`
- `property PlanningCollisionSceneData CollisionScene`
- `property RobotJointPositionList Goal`
- `property RobotSimulationTargetList HeldTargets`
- `property RobotMotionLimitList MotionLimits`
- `property JointSpacePlanningOptions Options`
- `property Double PositionTolerance`
- `property String PreparationDiagnostic`
- `property Double RotationTolerance`
- `property JointTrajectoryOptions Timing`

### `RobotPlanResult`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property BackendSelection Backends`
- `property EnumJointTrajectoryCurve Curve`
- `property JointSpacePlanningResult Planning`
- `property RobotBodyStateStamp SourceState`
- `property RobotTrajectoryPointList Trajectory`
- `method Boolean Succeeded()`

### `RobotSimulationSession`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Simulate.RobotBody, AnyCAD.Simulate.BackendSelection)`
- `ctor Void .ctor(AnyCAD.Simulate.RobotBody)`
- `method Void Cancel()`
- `method AnyCAD.Simulate.TreeIkRequest CreateIkRequest(AnyCAD.Simulate.RobotSimulationTargetList)`
- `method AnyCAD.Simulate.RobotPlanRequest CreatePlanningRequest(AnyCAD.Simulate.RobotJointPositionList, AnyCAD.Simulate.RobotMotionLimitList)`
- `method AnyCAD.Simulate.BackendSelection GetBackends()`
- `method System.String GetDiagnostic()`
- `method AnyCAD.Simulate.RobotBodyStateStamp GetSourceState()`
- `method Boolean IsValid()`
- `method AnyCAD.Simulate.RobotPlanResult Plan(AnyCAD.Simulate.RobotPlanRequest)`
- `method AnyCAD.Simulate.TreeIkResult SolveIk(AnyCAD.Simulate.RobotSimulationTargetList)`

### `RobotSimulationTarget`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotFrameIndex FrameIndex`
- `property Matrix4d TargetInBody`

### `RobotSimulationTargetList`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotSimulationTarget])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotSimulationTargetList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotSimulationTarget Item`
- `method Void Add(AnyCAD.Simulate.RobotSimulationTarget)`
- `method Void AddRange(AnyCAD.Simulate.RobotSimulationTargetList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotSimulationTarget[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotSimulationTarget[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotSimulationTarget[], Int32, Int32)`
- `method RobotSimulationTargetListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotSimulationTargetList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotSimulationTarget)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotSimulationTargetList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotSimulationTargetList Repeat(AnyCAD.Simulate.RobotSimulationTarget, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotSimulationTargetList)`
- `method AnyCAD.Simulate.RobotSimulationTarget[] ToArray()`

### `RobotSimulationTargetListEnumerator`

所属章节：第 5 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotSimulationTargetList)`
- `property RobotSimulationTarget Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotSingleTipIkRequest`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotArmIndex ArmIndex`
- `property String Backend`
- `property RobotBody Body`
- `property UInt32 MaxIterations`
- `property Double PositionTolerance`
- `property Double RotationTolerance`
- `property RobotJointPositionList Seed`
- `property Double SolverTolerance`
- `property Matrix4d TargetInBase`

### `RobotSingleTipIkResult`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Algorithm`
- `property RobotArmIndex ArmIndex`
- `property String Backend`
- `property String Diagnostic`
- `property RobotJointPositionList JointPositions`
- `property Double PositionError`
- `property Double RotationError`
- `property RobotBodyStateStamp SourceState`
- `property EnumRobotIkStatus Status`
- `property Matrix4d TargetInBase`
- `method Boolean IsSuccess()`

### `RobotSingleTipIkSolver`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.RobotSingleTipIkResult Solve(AnyCAD.Simulate.RobotSingleTipIkRequest)`

### `RobotTrajectoryPlayer`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method System.String GetDiagnostic()`
- `method Double GetDuration()`
- `method AnyCAD.Simulate.EnumRobotPlaybackState GetState()`
- `method Double GetTime()`
- `method Boolean Load(AnyCAD.Simulate.RobotBody, AnyCAD.Simulate.RobotPlanResult)`
- `method Void Pause()`
- `method Boolean Play()`
- `method Boolean Seek(Double)`
- `method Boolean Stop()`
- `method Boolean Tick(Double)`

### `RobotTrajectoryPoint`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property RobotJointPositionList Positions`
- `property Double Time`

### `RobotTrajectoryPointList`

所属章节：第 5 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.RobotTrajectoryPoint])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.RobotTrajectoryPointList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property RobotTrajectoryPoint Item`
- `method Void Add(AnyCAD.Simulate.RobotTrajectoryPoint)`
- `method Void AddRange(AnyCAD.Simulate.RobotTrajectoryPointList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.RobotTrajectoryPoint[])`
- `method Void CopyTo(AnyCAD.Simulate.RobotTrajectoryPoint[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.RobotTrajectoryPoint[], Int32, Int32)`
- `method RobotTrajectoryPointListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.RobotTrajectoryPointList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.RobotTrajectoryPoint)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.RobotTrajectoryPointList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.RobotTrajectoryPointList Repeat(AnyCAD.Simulate.RobotTrajectoryPoint, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.RobotTrajectoryPointList)`
- `method AnyCAD.Simulate.RobotTrajectoryPoint[] ToArray()`

### `RobotTrajectoryPointListEnumerator`

所属章节：第 5 节。
- `ctor Void .ctor(AnyCAD.Simulate.RobotTrajectoryPointList)`
- `property RobotTrajectoryPoint Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `RobotUrdfLoadOptions`

所属章节：第 3 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Boolean CollisionFallback`
- `property Double LengthScale`
- `property String PackageRoot`

### `RotateAnimationClip`

所属章节：第 9 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.Vector3d, Double, Single, Single)`
- `ctor Void .ctor(AnyCAD.Foundation.SceneNode, AnyCAD.Foundation.Vector3d, AnyCAD.Foundation.Vector3d, Double, Single, Single)`
- `method Double GetAngle()`
- `method AnyCAD.Foundation.Vector3d GetDirection()`
- `method AnyCAD.Foundation.SceneNode GetNode()`
- `method Boolean Play(Single)`
- `method Void Reset()`
- `method Void SetAngle(Double)`
- `method Void SetDirection(AnyCAD.Foundation.Vector3d)`
- `method Void SetNode(AnyCAD.Foundation.SceneNode)`

### `SimulateInstance`

所属章节：第 2 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `method Void Destroy()`
- `method AnyCAD.Simulate.SimulateInstance GetInstance()`
- `method Void Initialize()`
- `method AnyCAD.Simulate.SimulateInstance Instance()`
- `method Boolean InternalRegisterSdk(System.String, System.String, System.String, System.String, System.String)`
- `method Boolean RegisterSDK(System.String, System.String, System.String)`

### `SimulationBackends`

所属章节：第 2 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `method AnyCAD.Simulate.BackendDescriptor Describe(AnyCAD.Simulate.EnumSimulationBackendKind, System.String)`
- `method AnyCAD.Simulate.BackendSelection GetDefaults()`
- `method AnyCAD.Simulate.BackendDescriptorList List(AnyCAD.Simulate.EnumSimulationBackendKind, Boolean)`
- `method AnyCAD.Simulate.BackendDescriptorList List(AnyCAD.Simulate.EnumSimulationBackendKind)`
- `method Boolean LoadPlugin(System.String)`
- `method AnyCAD.Simulate.BackendSelection Resolve(AnyCAD.Simulate.BackendSelection)`
- `method Boolean SetDefaults(AnyCAD.Simulate.BackendSelection)`

### `TreeIkJoint`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property Boolean Active`
- `property Vector3d Axis`
- `property UInt32 ChildLinkIndex`
- `property Boolean HasPositionLimits`
- `property UInt32 JointIndex`
- `property EnumRobotJointType JointType`
- `property Double LowerLimit`
- `property Matrix4d Origin`
- `property UInt32 ParentLinkIndex`
- `property Double Position`
- `property Double UpperLimit`

### `TreeIkJointList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.TreeIkJoint])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkJointList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property TreeIkJoint Item`
- `method Void Add(AnyCAD.Simulate.TreeIkJoint)`
- `method Void AddRange(AnyCAD.Simulate.TreeIkJointList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkJoint[])`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkJoint[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.TreeIkJoint[], Int32, Int32)`
- `method TreeIkJointListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.TreeIkJointList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.TreeIkJoint)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.TreeIkJointList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.TreeIkJointList Repeat(AnyCAD.Simulate.TreeIkJoint, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.TreeIkJointList)`
- `method AnyCAD.Simulate.TreeIkJoint[] ToArray()`

### `TreeIkJointListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkJointList)`
- `property TreeIkJoint Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `TreeIkJointPosition`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt32 JointIndex`
- `property Double Position`

### `TreeIkJointPositionList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.TreeIkJointPosition])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkJointPositionList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property TreeIkJointPosition Item`
- `method Void Add(AnyCAD.Simulate.TreeIkJointPosition)`
- `method Void AddRange(AnyCAD.Simulate.TreeIkJointPositionList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkJointPosition[])`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkJointPosition[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.TreeIkJointPosition[], Int32, Int32)`
- `method TreeIkJointPositionListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.TreeIkJointPositionList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.TreeIkJointPosition)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.TreeIkJointPositionList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.TreeIkJointPositionList Repeat(AnyCAD.Simulate.TreeIkJointPosition, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.TreeIkJointPositionList)`
- `method AnyCAD.Simulate.TreeIkJointPosition[] ToArray()`

### `TreeIkJointPositionListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkJointPositionList)`
- `property TreeIkJointPosition Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `TreeIkRequest`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Backend`
- `property Double Damping`
- `property TreeIkJointList Joints`
- `property UInt32 MaxIterations`
- `property Double PositionTolerance`
- `property Double RotationTolerance`
- `property UInt32 SolveBaseLinkIndex`
- `property Double SolverTolerance`
- `property TreeIkTargetList Targets`

### `TreeIkResidual`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt32 FrameIndex`
- `property Double PositionError`
- `property Double RotationError`

### `TreeIkResidualList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.TreeIkResidual])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkResidualList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property TreeIkResidual Item`
- `method Void Add(AnyCAD.Simulate.TreeIkResidual)`
- `method Void AddRange(AnyCAD.Simulate.TreeIkResidualList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkResidual[])`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkResidual[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.TreeIkResidual[], Int32, Int32)`
- `method TreeIkResidualListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.TreeIkResidualList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.TreeIkResidual)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.TreeIkResidualList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.TreeIkResidualList Repeat(AnyCAD.Simulate.TreeIkResidual, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.TreeIkResidualList)`
- `method AnyCAD.Simulate.TreeIkResidual[] ToArray()`

### `TreeIkResidualListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkResidualList)`
- `property TreeIkResidual Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `TreeIkResult`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property String Algorithm`
- `property String Backend`
- `property String Diagnostic`
- `property UInt32 Iterations`
- `property TreeIkJointPositionList JointPositions`
- `property TreeIkResidualList Residuals`
- `property EnumTreeIkStatus Status`
- `method Boolean IsSuccess()`

### `TreeIkTarget`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property UInt32 FrameIndex`
- `property UInt32 LinkIndex`
- `property Matrix4d Origin`
- `property Matrix4d TargetInBase`

### `TreeIkTargetList`

所属章节：第 4 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor(System.Collections.IEnumerable)`
- `ctor Void .ctor(System.Collections.Generic.IEnumerable`1[AnyCAD.Simulate.TreeIkTarget])`
- `ctor Void .ctor()`
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkTargetList)`
- `ctor Void .ctor(Int32)`
- `property Int32 Capacity`
- `property Int32 Count`
- `property Boolean IsEmpty`
- `property Boolean IsFixedSize`
- `property Boolean IsReadOnly`
- `property Boolean IsSynchronized`
- `property TreeIkTarget Item`
- `method Void Add(AnyCAD.Simulate.TreeIkTarget)`
- `method Void AddRange(AnyCAD.Simulate.TreeIkTargetList)`
- `method Void Clear()`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkTarget[])`
- `method Void CopyTo(AnyCAD.Simulate.TreeIkTarget[], Int32)`
- `method Void CopyTo(Int32, AnyCAD.Simulate.TreeIkTarget[], Int32, Int32)`
- `method TreeIkTargetListEnumerator GetEnumerator()`
- `method AnyCAD.Simulate.TreeIkTargetList GetRange(Int32, Int32)`
- `method Void Insert(Int32, AnyCAD.Simulate.TreeIkTarget)`
- `method Void InsertRange(Int32, AnyCAD.Simulate.TreeIkTargetList)`
- `method Void RemoveAt(Int32)`
- `method Void RemoveRange(Int32, Int32)`
- `method AnyCAD.Simulate.TreeIkTargetList Repeat(AnyCAD.Simulate.TreeIkTarget, Int32)`
- `method Void Reverse(Int32, Int32)`
- `method Void Reverse()`
- `method Void SetRange(Int32, AnyCAD.Simulate.TreeIkTargetList)`
- `method AnyCAD.Simulate.TreeIkTarget[] ToArray()`

### `TreeIkTargetListEnumerator`

所属章节：第 4 节。
- `ctor Void .ctor(AnyCAD.Simulate.TreeIkTargetList)`
- `property TreeIkTarget Current`
- `method Boolean MoveNext()`
- `method Void Reset()`

### `VhacdOptions`

所属章节：第 7 节。
- `ctor Void .ctor(IntPtr, Boolean)`
- `ctor Void .ctor()`
- `property EnumVhacdFillMode FillMode`
- `property UInt32 MaxConvexHulls`
- `property UInt32 MaxNumVerticesPerCH`
- `property UInt32 MaxRecursionDepth`
- `property Double MinimumVolumePercentErrorAllowed`
- `property UInt32 Resolution`
- `property Boolean ShrinkWrap`
- `method Boolean IsValid()`
