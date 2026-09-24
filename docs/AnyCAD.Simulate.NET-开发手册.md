# AnyCAD.Simulate.NET 开发手册

> **API 基线：** NuGet.org `AnyCAD.Simulate.NET 2026.9.22.1736`，以包内 `lib/net80/AnyCAD.Simulate.NET.dll` 的公开类型和签名为准。本文是按任务查找的 .NET 工具手册，覆盖 RapidRobot 未使用的功能。

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

参数示例使用一套参数预设值；默认 `ShrinkWrap=true`、`FillMode=FloodFill`。输入必须在预期的 Link 局部单位中；URDF 米与 `RobotBody` 毫米混用会形成看似成功、实际错位的碰撞体。取消或失败不应覆盖调用方此前确认的结果。

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
