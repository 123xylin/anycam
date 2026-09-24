# 用 RapidRobot 搭建可交互的机器人避障演示：从 Link 凸包到 Kr500 末端路径

一台机器人能显示在屏幕上，并不代表它会躲避工装。RapidRobot 这次把碰撞体生成、场景障碍物、碰撞豁免和末端路径规划放在同一个可操作流程里。下面以 Kr500 为例，从空场景走到一条真正经过碰撞检查的轨迹。

> 📸 截图位 01：RapidRobot 主窗口、加载后的 Kr500 与「Collision demo」入口。

## 第一步：准备环境并加载 Kr500

安装 .NET 8 SDK，在仓库目录执行：

```powershell
dotnet restore WinForms\RapidRobot\RapidRobot.csproj --source https://api.nuget.org/v3/index.json
dotnet build WinForms\RapidRobot\RapidRobot.csproj -c Release --no-restore
```

项目固定使用 NuGet.org 的 `AnyCAD.Rapid.NET` 和 `AnyCAD.Simulate.NET` **2026.9.22.1736**。运行 `WinForms\bin\Release\net8.0-windows\RapidRobot.exe`，通过「Open」选择 `Model\Kr500\index.urdf`，再打开「Collision demo」。窗口会设置一组非奇异的演示姿态：AXIS2 为 `-0.45` rad、AXIS3 为 `0.6` rad，并添加一个可编辑的示例障碍物。

Kr500 的 URDF 长度按米填写，`RobotIO.Load` 默认转成内部毫米。演示窗口中的位置、尺寸与末端偏移都以 **毫米** 表示。示例障碍物中心为 `(2750, 0, 1470)`，尺寸为 `300 × 300 × 400`。

> 📸 截图位 02：加载 Kr500 后的演示姿态和橙色障碍物。

## 第二步：把 Link 可视网格变成多个凸包

在「Link convex hulls」选择 AXIS2、AXIS3、AXIS6，逐个点击「Generate / regenerate」。界面显示 V-HACD 进度；可以取消、查看黄色待确认预览，再点击「Confirm preview」。确认后的蓝色凸包才进入规划碰撞场景。重新生成失败时，先前确认的碰撞体仍保留；「Delete」会移除该 Link 的凸包。

这组参数沿用 AnyDesigner.exe 的“快速”预设：最多 8 个凸包、10 万体素、2.5% 体积误差、递归深度 8、每包最多 32 个顶点。其余保持 `VhacdOptions` 默认值，包括 `ShrinkWrap=true` 与 `FillMode=FloodFill`。

```csharp
using var mesh = UrdfVisualMesh.Load(urdfFile, linkName);
using var options = new VhacdOptions {
    MaxConvexHulls = 8, Resolution = 100000,
    MinimumVolumePercentErrorAllowed = 2.5,
    MaxRecursionDepth = 8, MaxNumVerticesPerCH = 32
};
using var session = new ConvexDecompositionSession(mesh, options);
using var result = session.Compute();
```

`UrdfVisualMesh` 读取 URDF 引用的 STL 三角面，并应用 mesh scale、visual origin 与 SDK 默认的 1000 倍长度转换，得到 **Link 局部坐标**中的网格。每个 V-HACD 结果单独建为 `ConvexHull` 形状，组合到一个绑定该 Link 的 `PlanningCollisionBody`；这样预览与碰撞计算使用同一批凸包顶点。

```csharp
using var shape = new PlanningCollisionShape {
    SourceId = sourceId,
    Type = EnumPlanningCollisionShapeType.ConvexHull
};
// 将一个 V-HACD 结果的 Vertices / Indices 填入 shape。
shapes.Add(shape);
bindings.Add(new RobotCollisionBinding {
    OwnerId = ownerId, LinkIndex = linkIndex, LinkToBody = identity
});
```

> 📸 截图位 03：AXIS6 的黄色待确认凸包预览。
>
> 📸 截图位 04：AXIS2、AXIS3、AXIS6 已确认的蓝色凸包。

## 第三步：查看和编辑相邻 Link 的碰撞豁免

「Collision exclusions」列出被忽略的实体对，也明确标出相邻 Link。相邻 Link 在运动中通常接触，因此默认豁免。选中一对后点击「Check pair」即可取消豁免；点击「Ignore pair」恢复。这里的更改会进入下一次规划请求，并改变实际检测结果。

```csharp
using var ignoredPairs = scene.IgnoredPairs;
ignoredPairs.Clear();
foreach (var pair in ignored)
    if (present.Contains(pair.First) && present.Contains(pair.Second))
        ignoredPairs.Add(new PlanningCollisionIgnoredPair {
            FirstId = new ObjectId(pair.First),
            SecondId = new ObjectId(pair.Second)
        });
```

每次规划都从当前界面状态重新生成碰撞场景，避免“列表里已改、算法仍用旧值”。自动验收中，两块故意重叠的相邻 Link 凸包在豁免时可规划，取消豁免后被判为起点碰撞。

> 📸 截图位 05：相邻 Link 豁免列表及取消、恢复操作。

## 第四步：编辑场景实体与它的碰撞体

「Scene entities and collision boxes」可以添加、删除或修改盒状障碍物的中心位置与宽、深、高。可见橙色盒子和规划中的 `PlanningCollisionShape` 都读取同一组数值。场景编辑后再次规划，即使用最新位置与尺寸。

```csharp
using var collisionBody = new PlanningCollisionBody {
    OwnerId = ownerId,
    OwnerName = item.Name,
    OwnerType = EnumPlanningCollisionOwnerType.Environment,
    WorldTransform = world,
    IncludeWorldTransformInFingerprint = true
};
using var shape = new PlanningCollisionShape {
    SourceId = sourceId,
    Type = EnumPlanningCollisionShapeType.Box,
    Size = new Vector3d(item.Width, item.Depth, item.Height)
};
```

> 📸 截图位 06：调整障碍物位置和尺寸，模型中的橙色盒子同步变化。

## 第五步：规划 Kr500 末端绕行路径

末端目标与关节目标分开输入。默认目标是当前末端沿世界 X 方向移动 `-450 mm`，保留当前姿态。程序先解 IK 得到完整手臂的关节目标，再用已确认的 Link 凸包、场景障碍物和豁免对构造规划请求。示例将碰撞路径采样间隔设为 `0.05 rad`，让中途障碍不会因 SDK 较大的默认间隔而漏检。

```csharp
using var ik = session.SolveIk(targets);
if (!ik.IsSuccess()) {
    status.Text = "IK " + ik.Status + ": " + ik.Diagnostic;
    return;
}
using var request = session.CreatePlanningRequest(goals, limits);
using (var options = request.Options) options.CollisionResolution = 0.05;
model.ApplyTo(request);
using var result = await Task.Run(() => session.Plan(request));
if (result.Succeeded()) player.Load(body, result);
```

点击「Plan tip path」后查看诊断；成功时使用「Play」「Pause」「Stop / cancel」和时间滑块观察轨迹。目标不可达、起点或终点碰撞、后端不可用、取消或超时会显示状态和诊断，不加载失败轨迹。对默认 Kr500 场景的自动验收确认：关节直线中点位于障碍物内，而 OMPL/FCL 给出 4 个路径点；对路径段另外取 9 个状态进行碰撞有效性检查。删除障碍物或 AXIS6 凸包会恢复直达路径，重新加入凸包又会绕行。

> 📸 截图位 07：末端目标、规划成功提示与绕行轨迹。
>
> 📸 截图位 08：播放轨迹时，Kr500 从障碍物旁经过的不同时间点。

这次示例把可视网格、凸包、碰撞场景、IK 与轨迹播放串成了一个运行期工作流。编辑结果保留在当前会话中；重新加载机器人会清空它们。项目没有写回 URDF，也没有增加 JSON 保存。
