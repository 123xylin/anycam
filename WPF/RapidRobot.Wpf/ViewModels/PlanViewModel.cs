using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace RapidRobot.Wpf.ViewModels
{
    /// <summary>
    /// 路径规划 Tab：目标位姿、后端选择、SDK 单关节规划、无碰撞路径规划。
    /// 共享依赖通过根 MainWindowViewModel 访问；pendingPlan/player 由根 VM 持有，
    /// 播放器（轨迹加载/播放/重置）通过根 VM 的统一入口操作。
    /// </summary>
    public sealed partial class PlanViewModel : ObservableObject
    {
        public sealed record JointChoice(uint Index, string Name)
        {
            public override string ToString() => Index + ": " + Name;
        }

        public sealed record BackendChoice(string Name, bool Available, string Detail)
        {
            public override string ToString() => Name + (Available ? " — 可用" : " — 不可用");
        }

        private readonly MainWindowViewModel root;
        private bool updatingUi;

        // ---- 目标位姿 ----

        [ObservableProperty]
        private string planTargetX = "795.000";

        [ObservableProperty]
        private string planTargetY = "380.000";

        [ObservableProperty]
        private string planTargetZ = "1080.000";

        [ObservableProperty]
        private string planTargetRx = "0.000°";

        [ObservableProperty]
        private string planTargetRy = "-90.000°";

        [ObservableProperty]
        private string planTargetRz = "0.000°";

        // ---- 规划配置 ----

        [ObservableProperty]
        private string collisionResolution = "0.05 rad";

        [ObservableProperty]
        private string planTimeout = "10.0 s";

        // ---- 后端 ----

        [ObservableProperty]
        private ObservableCollection<BackendChoice> ikBackendItems = new();

        [ObservableProperty]
        private ObservableCollection<BackendChoice> plannerItems = new();

        [ObservableProperty]
        private ObservableCollection<BackendChoice> collisionBackendItems = new();

        [ObservableProperty]
        private BackendChoice? selectedIkBackend;

        [ObservableProperty]
        private BackendChoice? selectedPlanner;

        [ObservableProperty]
        private BackendChoice? selectedCollisionBackend;

        // ---- SDK 单关节规划 ----

        [ObservableProperty]
        private ObservableCollection<JointChoice> sdkJointItems = new();

        [ObservableProperty]
        private JointChoice? selectedSdkJoint;

        [ObservableProperty]
        private string sdkGoal = "0.0000";

        [ObservableProperty]
        private string sdkVelocity = "1.000";

        [ObservableProperty]
        private string sdkAcceleration = "2.000";

        [ObservableProperty]
        private string sdkCheckIkStatusText = "";

        // ---- 规划结果 ----

        [ObservableProperty]
        private string planStatusText = "等待规划";

        [ObservableProperty]
        private string planBackendText = "OMPL / FCL";

        [ObservableProperty]
        private string pathLengthText = "—";

        [ObservableProperty]
        private string pathDurationText = "—";

        [ObservableProperty]
        private string pathSamplesText = "—";

        // ---- 互斥状态转发（根 VM 计算） ----

        public bool CanInteract => root.CanInteract;
        public bool CanPlan => root.CanPlan;

        public PlanViewModel(MainWindowViewModel root)
        {
            this.root = root;
            root.PropertyChanged += OnRootPropertyChanged;
            InitializeBackends();
        }

        private void OnRootPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.CanInteract):
                    OnPropertyChanged(nameof(CanInteract));
                    break;
                case nameof(MainWindowViewModel.CanPlan):
                    OnPropertyChanged(nameof(CanPlan));
                    break;
            }
        }

        // ============ 状态刷新 ============

        public void RefreshAllUi()
        {
            RefreshSdkJointCombo();
        }

        public void UpdateDefaultTarget()
        {
            if (!root.TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PlanTargetX = MainWindowViewModel.Format(x - 450.0);
            PlanTargetY = MainWindowViewModel.Format(y);
            PlanTargetZ = MainWindowViewModel.Format(z);
            PlanTargetRx = MainWindowViewModel.Format(rx * 180.0 / Math.PI) + "°";
            PlanTargetRy = MainWindowViewModel.Format(ry * 180.0 / Math.PI) + "°";
            PlanTargetRz = MainWindowViewModel.Format(rz * 180.0 / Math.PI) + "°";
        }

        public void ResetPathMetrics()
        {
            PathLengthText = "—";
            PathDurationText = "—";
            PathSamplesText = "—";
        }

        // ============ 后端选择 ============

        private void InitializeBackends()
        {
            FillBackendCombo(IkBackendItems, EnumSimulationBackendKind.Ik, root.Backends.Ik);
            FillBackendCombo(PlannerItems, EnumSimulationBackendKind.Planner, root.Backends.Planner);
            FillBackendCombo(CollisionBackendItems, EnumSimulationBackendKind.Collision, root.Backends.Collision);
        }

        private void FillBackendCombo(ObservableCollection<BackendChoice> combo,
            EnumSimulationBackendKind kind, string selectedName)
        {
            updatingUi = true;
            try
            {
                combo.Clear();
                var selectedIndex = -1;
                using var descriptors = SimulationBackends.List(kind, true);
                foreach (var descriptor in descriptors)
                {
                    using (descriptor)
                    {
                        combo.Add(new BackendChoice(descriptor.Name, descriptor.Available,
                            descriptor.Available ? descriptor.Algorithm : descriptor.Diagnostic));
                        if (descriptor.Name == selectedName)
                            selectedIndex = combo.Count - 1;
                    }
                }
                if (selectedIndex >= 0 && selectedIndex < combo.Count)
                {
                    if (kind == EnumSimulationBackendKind.Ik)
                        SelectedIkBackend = combo[selectedIndex];
                    else if (kind == EnumSimulationBackendKind.Planner)
                        SelectedPlanner = combo[selectedIndex];
                    else
                        SelectedCollisionBackend = combo[selectedIndex];
                }
                else if (combo.Count > 0)
                {
                    if (kind == EnumSimulationBackendKind.Ik)
                        SelectedIkBackend = combo[0];
                    else if (kind == EnumSimulationBackendKind.Planner)
                        SelectedPlanner = combo[0];
                    else
                        SelectedCollisionBackend = combo[0];
                }
            }
            finally
            {
                updatingUi = false;
            }
        }

        partial void OnSelectedIkBackendChanged(BackendChoice? value) => ApplyBackendSelection();

        partial void OnSelectedPlannerChanged(BackendChoice? value) => ApplyBackendSelection();

        partial void OnSelectedCollisionBackendChanged(BackendChoice? value) => ApplyBackendSelection();

        private void ApplyBackendSelection()
        {
            if (updatingUi)
                return;
            if (SelectedIkBackend == null || SelectedPlanner == null ||
                SelectedCollisionBackend == null)
                return;
            var ik = SelectedIkBackend.Name;
            var planner = SelectedPlanner.Name;
            var collision = SelectedCollisionBackend.Name;
            try
            {
                root.Backends.Ik = ik;
                root.Backends.Planner = planner;
                root.Backends.Collision = collision;
                using var defaults = new BackendSelection {
                    Ik = ik,
                    Planner = planner,
                    Collision = collision
                };
                SimulationBackends.SetDefaults(defaults);
                PlanBackendText = planner + " / " + collision;
            }
            catch (Exception error)
            {
                root.SetStatus("后端设置失败：" + error.Message, true);
            }
        }

        // ============ SDK 单关节 ============

        public void RefreshSdkJointCombo()
        {
            var controller = root.Workspace.Controller;
            var selected = SelectedSdkJoint;
            updatingUi = true;
            try
            {
                SdkJointItems.Clear();
                if (controller != null)
                {
                    for (uint i = 0; i < controller.GetJointCount(controller.SelectedArmIndex); ++i)
                    {
                        if (controller.GetJointType(controller.SelectedArmIndex, i) == EnumRobotJointType.Fixed)
                            continue;
                        SdkJointItems.Add(new JointChoice(i,
                            controller.GetJointName(controller.SelectedArmIndex, i)));
                    }
                    if (selected != null)
                    {
                        foreach (var item in SdkJointItems)
                        {
                            if (item.Index == selected.Index)
                            {
                                SelectedSdkJoint = item;
                                break;
                            }
                        }
                    }
                    if (SelectedSdkJoint == null && SdkJointItems.Count > 0)
                        SelectedSdkJoint = SdkJointItems[0];
                }
            }
            finally
            {
                updatingUi = false;
            }
        }

        partial void OnSelectedSdkJointChanged(JointChoice? value)
        {
            if (updatingUi || value == null)
                return;
            LoadSdkJointDefaults();
        }

        private void LoadSdkJointDefaults()
        {
            var body = root.Workspace.Body;
            if (body == null || SelectedSdkJoint is not { } choice)
                return;
            using var index = new RobotJointIndex(choice.Index);
            using var joint = body.GetJoint(index);
            SdkGoal = body.GetJointPosition(index).ToString("0.0000", CultureInfo.InvariantCulture);
            var velocity = joint.GetMaxVelocity() > 0 ? joint.GetMaxVelocity() : 1;
            SdkVelocity = velocity.ToString("0.000", CultureInfo.InvariantCulture);
            SdkAcceleration = (2 * velocity).ToString("0.000", CultureInfo.InvariantCulture);
        }

        // ============ 命令：目标位姿 ============

        [RelayCommand]
        private void UseCurrentPose()
        {
            if (!root.TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PlanTargetX = MainWindowViewModel.Format(x);
            PlanTargetY = MainWindowViewModel.Format(y);
            PlanTargetZ = MainWindowViewModel.Format(z);
            PlanTargetRx = MainWindowViewModel.Format(rx * 180.0 / Math.PI) + "°";
            PlanTargetRy = MainWindowViewModel.Format(ry * 180.0 / Math.PI) + "°";
            PlanTargetRz = MainWindowViewModel.Format(rz * 180.0 / Math.PI) + "°";
            root.SetStatus("目标位姿已复制当前末端姿态");
        }

        // ============ 命令：路径规划 ============

        [RelayCommand]
        private async Task PlanPathAsync()
        {
            if (!root.EnsureNoActiveOperation())
                return;
            await root.RunTrackedAsync(PlanPathCore);
        }

        private async Task PlanPathCore()
        {
            var body = root.Workspace.Body;
            var model = root.Workspace.Collision;
            if (body == null || model == null)
            {
                PlanStatusText = "请先加载机器人。";
                return;
            }
            if (!TryReadPlanTarget(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
            {
                PlanStatusText = "目标位姿包含无效数字。";
                return;
            }
            if (!MainWindowViewModel.TryParseNumber(CollisionResolution, out var collisionResolution) ||
                collisionResolution <= 0)
            {
                PlanStatusText = "碰撞采样间隔必须是大于 0 的有限数值。";
                return;
            }
            if (!MainWindowViewModel.TryParseNumber(PlanTimeout, out var timeoutSeconds) ||
                timeoutSeconds <= 0)
            {
                PlanStatusText = "规划超时必须是大于 0 的有限数值。";
                return;
            }
            if (model.LinkHulls.Count == 0)
            {
                PlanStatusText = "请先生成并确认至少一个 Link 凸包。";
                return;
            }
            root.ResetPlayer();
            PlanStatusText = "正在规划无碰撞路径…";
            try
            {
                using var session = new RobotSimulationSession(body, root.Backends);
                root.PendingPlan = session;
                if (!session.IsValid())
                    throw new InvalidOperationException(session.GetDiagnostic());
                var controller = root.Workspace.Controller ??
                    throw new InvalidOperationException("机器人控制器不可用。");
                using var armIndex = new RobotArmIndex(controller.SelectedArmIndex);
                using var arm = body.GetArm(armIndex);
                if (arm == null)
                    throw new InvalidOperationException("无法访问当前机械臂。");
                using var tipFrame = arm.GetTipFrameIndex();
                if (tipFrame == null || !tipFrame.IsValid())
                    throw new InvalidOperationException("机械臂末端 Frame 无效。");
                using var targetWorld = RobotAssemblyPose.Create(x, y, z,
                    rx * Math.PI / 180.0, ry * Math.PI / 180.0, rz * Math.PI / 180.0);
                using var robotModel = body.GetModel();
                using var rootIndex = robotModel.GetRootLinkIndex();
                if (rootIndex == null || !rootIndex.IsValid())
                    throw new InvalidOperationException("机器人根 Link 无效。");
                using var rootWorld = body.GetLinkTransform(rootIndex, true);
                using var inverseRoot = rootWorld.inversed();
                using var targetInBody = inverseRoot.multiplied(targetWorld);
                using var target = new RobotSimulationTarget {
                    FrameIndex = tipFrame,
                    TargetInBody = targetInBody
                };
                using var targets = new RobotSimulationTargetList();
                targets.Add(target);
                using var ik = session.SolveIk(targets);
                if (!ik.IsSuccess())
                    throw new InvalidOperationException("IK " + ik.Status + ": " + ik.Diagnostic);
                using var goals = new RobotJointPositionList();
                using var limits = new RobotMotionLimitList();
                using (var solved = ik.JointPositions)
                {
                    foreach (var solvedPosition in solved)
                    {
                        using (solvedPosition)
                        using (var jointIndex = new RobotJointIndex(solvedPosition.JointIndex))
                        using (var joint = body.GetJoint(jointIndex))
                        {
                            if (joint == null)
                                throw new InvalidOperationException("IK 返回了无效关节索引。");
                            using var goal = new RobotJointPosition {
                                JointIndex = jointIndex,
                                Position = solvedPosition.Position
                            };
                            using var limit = new RobotMotionLimit {
                                JointIndex = jointIndex,
                                MaxVelocity = joint.GetMaxVelocity() > 0 ? joint.GetMaxVelocity() : 1,
                                MaxAcceleration = joint.GetMaxVelocity() > 0 ? 2 * joint.GetMaxVelocity() : 2
                            };
                            goals.Add(goal);
                            limits.Add(limit);
                        }
                    }
                }
                if (goals.Count == 0)
                    throw new InvalidOperationException("IK 没有返回可规划关节。");
                using var request = session.CreatePlanningRequest(goals, limits);
                using (var options = request.Options)
                {
                    options.CollisionResolution = collisionResolution;
                    options.TimeoutSeconds = timeoutSeconds;
                }
                model.ApplyTo(request);
                using var result = await Task.Run(() => session.Plan(request));
                if (root.IsClosing)
                    return;
                if (!result.Succeeded())
                {
                    using var planning = result.Planning;
                    PlanStatusText = planning.Status + ": " + planning.Diagnostic;
                    return;
                }
                using (var planning = result.Planning)
                using (var path = planning.Path)
                {
                    PathSamplesText = path.Count.ToString(CultureInfo.InvariantCulture);
                    PathLengthText = CalculatePathLength(path).ToString("0.000", CultureInfo.InvariantCulture);
                }
                string backendText;
                using (var selectedBackends = result.Backends)
                    backendText = selectedBackends.Planner + " / " + selectedBackends.Collision;
                if (!root.Player.Load(body, result))
                    throw new InvalidOperationException(root.Player.GetDiagnostic());
                root.RefreshPlaybackUi();
                PathDurationText = root.Player.GetDuration().ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlanStatusText = "无碰撞路径已就绪";
                PlanBackendText = backendText;
            }
            catch (Exception error)
            {
                if (!root.IsClosing)
                    PlanStatusText = "路径规划失败：" + error.Message;
            }
            finally
            {
                root.PendingPlan = null;
                root.RefreshInteractionState();
            }
        }

        private static double CalculatePathLength(DoubleList2 path)
        {
            var length = 0.0;
            for (var segment = 0; segment + 1 < path.Count; ++segment)
            using (var from = path[segment])
            using (var to = path[segment + 1])
            {
                var count = Math.Min(from.Count, to.Count);
                for (var joint = 0; joint < count; ++joint)
                {
                    var difference = to[joint] - from[joint];
                    length += Math.Abs(difference);
                }
            }
            return length;
        }

        // ============ 命令：SDK 单关节规划 / IK 检查 ============

        [RelayCommand]
        private async Task PlanSingleJointAsync()
        {
            var body = root.Workspace.Body;
            if (body == null)
            {
                PlanStatusText = "请先加载机器人。";
                return;
            }
            if (SelectedSdkJoint is not { } choice)
            {
                PlanStatusText = "请选择要规划的关节。";
                return;
            }
            if (!MainWindowViewModel.TryParseNumber(SdkGoal, out var goalValue) ||
                !MainWindowViewModel.TryParseNumber(SdkVelocity, out var velocityValue) ||
                !MainWindowViewModel.TryParseNumber(SdkAcceleration, out var accelerationValue) ||
                velocityValue <= 0 || accelerationValue <= 0)
            {
                PlanStatusText = "速度与加速度必须大于 0，目标值必须是有效数字。";
                return;
            }
            if (!root.EnsureNoActiveOperation())
                return;
            root.ResetPlayer();
            PlanStatusText = "正在规划单关节轨迹…";
            try
            {
                using var session = new RobotSimulationSession(body, root.Backends);
                root.PendingPlan = session;
                if (!session.IsValid())
                    throw new InvalidOperationException(session.GetDiagnostic());
                using var index = new RobotJointIndex(choice.Index);
                using var goals = new RobotJointPositionList();
                using var position = new RobotJointPosition { JointIndex = index, Position = goalValue };
                goals.Add(position);
                using var motionLimits = new RobotMotionLimitList();
                using var limit = new RobotMotionLimit {
                    JointIndex = index,
                    MaxVelocity = velocityValue,
                    MaxAcceleration = accelerationValue
                };
                motionLimits.Add(limit);
                using var request = session.CreatePlanningRequest(goals, motionLimits);
                using var result = await Task.Run(() => session.Plan(request));
                if (root.IsClosing)
                    return;
                if (!result.Succeeded())
                {
                    using var planning = result.Planning;
                    PlanStatusText = planning.Status + ": " + planning.Diagnostic;
                    return;
                }
                using (var planning = result.Planning)
                using (var path = planning.Path)
                    PathSamplesText = path.Count.ToString(CultureInfo.InvariantCulture);
                if (!root.Player.Load(body, result))
                    throw new InvalidOperationException(root.Player.GetDiagnostic());
                root.RefreshPlaybackUi();
                PathDurationText = root.Player.GetDuration().ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlanStatusText = "单关节轨迹已就绪，点击播放开始演示。";
                PlanBackendText = root.Backends.Planner + " / " + root.Backends.Collision;
            }
            catch (Exception error)
            {
                if (!root.IsClosing)
                    PlanStatusText = "单关节规划失败：" + error.Message;
            }
            finally
            {
                root.PendingPlan = null;
                root.RefreshInteractionState();
            }
        }

        [RelayCommand]
        private void CheckAllTipIk()
        {
            var body = root.Workspace.Body;
            if (body == null)
            {
                PlanStatusText = "请先加载机器人。";
                return;
            }
            var report = new StringBuilder();
            try
            {
                using var session = new RobotSimulationSession(body, root.Backends);
                for (uint i = 0; i < body.GetArmCount(); ++i)
                {
                    using var armIndex = new RobotArmIndex(i);
                    using var arm = body.GetArm(armIndex);
                    if (arm == null)
                        continue;
                    using var frame = arm.GetTipFrameIndex();
                    using var transform = body.GetFrameTransform(frame, false);
                    using var targets = new RobotSimulationTargetList();
                    using var target = new RobotSimulationTarget { FrameIndex = frame, TargetInBody = transform };
                    targets.Add(target);
                    using var result = session.SolveIk(targets);
                    report.Append("Arm " + (i + 1) + " ");
                    report.Append(result.Backend + "/" + result.Algorithm + ": ");
                    report.Append(result.IsSuccess() ? "OK" : (result.Status + " " + result.Diagnostic));
                    report.AppendLine();
                }
                PlanStatusText = report.Length == 0 ? "没有可用的机械臂。" : report.ToString().TrimEnd();
                SdkCheckIkStatusText = "IK 后端：" + root.Backends.Ik;
            }
            catch (Exception error)
            {
                if (!root.IsClosing)
                    PlanStatusText = "IK 检查失败：" + error.Message;
            }
        }

        private bool TryReadPlanTarget(out double x, out double y, out double z,
            out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            return MainWindowViewModel.TryParseNumber(PlanTargetX, out x) &&
                MainWindowViewModel.TryParseNumber(PlanTargetY, out y) &&
                MainWindowViewModel.TryParseNumber(PlanTargetZ, out z) &&
                MainWindowViewModel.TryParseNumber(PlanTargetRx, out rx) &&
                MainWindowViewModel.TryParseNumber(PlanTargetRy, out ry) &&
                MainWindowViewModel.TryParseNumber(PlanTargetRz, out rz);
        }
    }
}
