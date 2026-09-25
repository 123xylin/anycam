using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace RapidRobot.Wpf.ViewModels
{
    public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        private sealed record TeachPoint(DateTimeOffset CreatedAt, double[] Position,
            double[] Orientation, double[] Joints);

        public sealed record LinkChoice(int Index, string Name, int HullCount)
        {
            public override string ToString() =>
                Name + (HullCount == 0 ? " · 待生成" : " · " + HullCount + " 凸包");
        }

        public sealed record LinkRowVm(string Name, int HullCount)
        {
            public string HullCountText => HullCount == 0 ? "—" : HullCount.ToString(CultureInfo.InvariantCulture);
            public string StatusText => HullCount == 0 ? "待生成" : "已确认";
            public Brush StatusBrush => HullCount == 0
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                : new SolidColorBrush(Color.FromRgb(21, 128, 61));
        }

        public sealed record ObstacleItem(CollisionDemoModel.Obstacle Value, string Name)
        {
            public override string ToString() => Name;
        }

        public sealed record OwnerChoice(ulong Id, string Name)
        {
            public override string ToString() => Name;
        }

        public sealed record JointChoice(uint Index, string Name)
        {
            public override string ToString() => Index + ": " + Name;
        }

        public sealed record BackendChoice(string Name, bool Available, string Detail)
        {
            public override string ToString() => Name + (Available ? " — 可用" : " — 不可用");
        }

        private readonly RobotWorkspaceSession workspace = new();
        private readonly BackendSelection backends;
        private readonly IRenderHost renderHost;
        private readonly Stopwatch playbackClock = new();
        private readonly List<TeachPoint> teachPoints = new();
        private RobotTrajectoryPlayer player = new();
        private RobotSimulationSession? pendingPlan;
        private ConvexDecompositionSession? pendingDecomposition;
        private Task? activeOperation;
        private (uint Index, List<CollisionDemoModel.Hull> Hulls)? candidate;
        private bool viewerReady;
        private bool loadingModel;
        private bool updatingUi;
        private bool updatingPlayback;
        private bool closing;
        private bool shutdownComplete;
        private string modelLabel = "未加载";
        private RobotTargetEditor? targetEditor;
        private GroupSceneNode? targetNode;

        // ---- observable UI state ----

        [ObservableProperty]
        private string sceneResourceName = "未加载";

        [ObservableProperty]
        private string sceneResourceStatus = "未加载";

        [ObservableProperty]
        private string robotDofText = "未加载";

        [ObservableProperty]
        private string globalStatusText = "等待加载机器人模型";

        [ObservableProperty]
        private Brush globalStatusBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));

        [ObservableProperty]
        private Brush robotConnectionBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));

        // 目录树折叠为方形按钮时显示（与 SceneTreeExpanded 相反）
        public Visibility SceneTreeCollapsedVisibility =>
            SceneTreeExpanded ? Visibility.Collapsed : Visibility.Visible;

        // 目录树面板可见性（默认展开）
        public Visibility SceneTreeVisibility =>
            SceneTreeExpanded ? Visibility.Visible : Visibility.Collapsed;

        partial void OnSceneTreeExpandedChanged(bool value)
        {
            OnPropertyChanged(nameof(SceneTreeVisibility));
            OnPropertyChanged(nameof(SceneTreeCollapsedVisibility));
        }

        [ObservableProperty]
        private Visibility viewerEmptyVisibility = Visibility.Visible;

        [ObservableProperty]
        private bool isLinkVisible = true;

        [ObservableProperty]
        private bool isTrajectoryVisible = true;

        [ObservableProperty]
        private bool isGridVisible = true;

        // 场景资源目录树折叠/展开（默认展开）
        [ObservableProperty]
        private bool sceneTreeExpanded = true;

        // 示教
        [ObservableProperty]
        private ObservableCollection<string> armItems = new();

        [ObservableProperty]
        private int selectedArmIndex = -1;

        [ObservableProperty]
        private double jogStep = 35;

        [ObservableProperty]
        private string jogStepText = "—";

        [ObservableProperty]
        private string[] jointValues = new string[6];

        [ObservableProperty]
        private bool[] jogEnabled = new bool[6];

        [ObservableProperty]
        private string poseX = "1245.000";

        [ObservableProperty]
        private string poseY = "380.000";

        [ObservableProperty]
        private string poseZ = "1080.000";

        [ObservableProperty]
        private string poseRx = "0.000°";

        [ObservableProperty]
        private string poseRy = "-90.000°";

        [ObservableProperty]
        private string poseRz = "0.000°";

        // 路径规划目标
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

        [ObservableProperty]
        private string collisionResolution = "0.05 rad";

        [ObservableProperty]
        private string planTimeout = "10.0 s";

        // 碰撞检查
        [ObservableProperty]
        private string collisionCoverageText = "0%";

        [ObservableProperty]
        private string collisionCoverageDetail = "尚未加载机器人";

        [ObservableProperty]
        private double collisionCoverageProgress;

        [ObservableProperty]
        private ObservableCollection<LinkChoice> collisionLinkItems = new();

        [ObservableProperty]
        private LinkChoice? selectedCollisionLink;

        [ObservableProperty]
        private ObservableCollection<LinkRowVm> collisionLinkRows = new();

        [ObservableProperty]
        private string generateHullsContent = "生成 / 重新生成";

        [ObservableProperty]
        private double decomposeProgress;

        [ObservableProperty]
        private ObservableCollection<ObstacleItem> obstacleItems = new();

        [ObservableProperty]
        private ObstacleItem? selectedObstacleItem;

        [ObservableProperty]
        private string obstacleX = "2750";

        [ObservableProperty]
        private string obstacleY = "0";

        [ObservableProperty]
        private string obstacleZ = "1470";

        [ObservableProperty]
        private string obstacleWidth = "300";

        [ObservableProperty]
        private string obstacleDepth = "300";

        [ObservableProperty]
        private string obstacleHeight = "400";

        [ObservableProperty]
        private string collisionStatusText = "等待操作";

        [ObservableProperty]
        private string ignoredPairCountText = "0 对已忽略";

        [ObservableProperty]
        private ObservableCollection<OwnerChoice> pairOwner1Items = new();

        [ObservableProperty]
        private ObservableCollection<OwnerChoice> pairOwner2Items = new();

        [ObservableProperty]
        private OwnerChoice? selectedOwner1;

        [ObservableProperty]
        private OwnerChoice? selectedOwner2;

        [ObservableProperty]
        private ObservableCollection<string> ignoredPairItems = new();

        // 后端
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

        // SDK 单关节规划
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

        // 规划结果
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

        // 播放
        [ObservableProperty]
        private double playbackPosition;

        [ObservableProperty]
        private double playbackMaximum = 1;

        [ObservableProperty]
        private string playbackTimeText = "0.000 / 0.000 s";

        [ObservableProperty]
        private string playbackPlayText = "播放";

        // 交互状态
        [ObservableProperty]
        private bool canInteract;

        [ObservableProperty]
        private bool canPlan;

        [ObservableProperty]
        private bool canGenerateHulls;

        [ObservableProperty]
        private bool canEditCollisionLink;

        [ObservableProperty]
        private bool canEditObstacle;

        [ObservableProperty]
        private bool canEditPair;

        [ObservableProperty]
        private bool canControlPlayback;

        [ObservableProperty]
        private bool canStopPlayback;

        [ObservableProperty]
        private bool canSaveTeachPoints;

        // 命令
        public ICommand[] JogNegativeCommands { get; }
        public ICommand[] JogPositiveCommands { get; }
        public ICommand IgnorePairCommand { get; }
        public ICommand CheckPairCommand { get; }

        public MainWindowViewModel(IRenderHost host)
        {
            renderHost = host;
            backends = SimulationBackends.GetDefaults();
            workspace.Changed += OnWorkspaceChanged;
            JogNegativeCommands = new ICommand[6];
            JogPositiveCommands = new ICommand[6];
            for (var i = 0; i < 6; ++i)
            {
                var index = i;
                JogNegativeCommands[i] = new RelayCommand(() => JogJoint(index, -1));
                JogPositiveCommands[i] = new RelayCommand(() => JogJoint(index, 1));
            }
            IgnorePairCommand = new RelayCommand(() => SetPairIgnored(true));
            CheckPairCommand = new RelayCommand(() => SetPairIgnored(false));
            InitializeBackends();
        }

        // ============ 渲染 / 生命周期 ============

        public void OnViewerReady()
        {
            if (viewerReady)
                return;
            viewerReady = true;
            renderHost.ShowCoordinateGrid(IsGridVisible);
            renderHost.SetStandardView();
            renderHost.RequestDraw(EnumUpdateFlags.Scene | EnumUpdateFlags.Camera);
            if (workspace.Body == null)
                LoadManualKr500();
        }

        private void OnWorkspaceChanged()
        {
            if (closing)
                return;
            try
            {
                RefreshAllUi();
                if (viewerReady)
                    renderHost.RequestDraw(EnumUpdateFlags.Scene);
            }
            catch (Exception error)
            {
                SetStatus("界面刷新失败：" + error.Message, true);
            }
        }

        public async Task ShutdownAsync()
        {
            if (shutdownComplete)
                return;
            closing = true;
            workspace.Changed -= OnWorkspaceChanged;
            pendingPlan?.Cancel();
            pendingDecomposition?.Cancel();
            var operation = activeOperation;
            if (operation != null)
            {
                try
                {
                    await operation;
                }
                catch (Exception error)
                {
                    Debug.WriteLine("Shutdown task: " + error.Message);
                }
            }
            TryCleanup(() => player.Stop());
            TryCleanup(player.Dispose);
            TryCleanup(workspace.Dispose);
            TryCleanup(ReleaseTargetEditor);
            TryCleanup(backends.Dispose);
            shutdownComplete = true;
        }

        private static void TryCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception error)
            {
                Debug.WriteLine("Cleanup: " + error.Message);
            }
        }

        public void Dispose() => TryCleanup(() =>
        {
            if (shutdownComplete)
                return;
            shutdownComplete = true;
            workspace.Changed -= OnWorkspaceChanged;
            workspace.Dispose();
            backends.Dispose();
            player.Dispose();
        });

        // ============ 状态刷新 ============

        private void RefreshAllUi()
        {
            var controller = workspace.Controller;
            var body = workspace.Body;
            SceneResourceName = body == null ? "未加载" : modelLabel + "_ROBOT";
            SceneResourceStatus = body == null ? "未加载" : "已加载";
            ViewerEmptyVisibility = body == null ? Visibility.Visible : Visibility.Collapsed;
            RobotConnectionBrush = body == null
                ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
                : new SolidColorBrush(Color.FromRgb(34, 197, 94));
            RobotDofText = controller == null
                ? "未加载"
                : modelLabel + " · " + controller.ArmCount + " ARM";
            GlobalStatusText = body == null ? "等待加载机器人模型" : "安全状态：可操作";
            RefreshInteractionState();
            RefreshArmUi();
            RefreshJointUi();
            RefreshPoseUi();
            RefreshCollisionUi();
            RefreshPlaybackUi();
        }

        private void RefreshInteractionState()
        {
            var controller = workspace.Controller;
            var idle = activeOperation == null && !loadingModel &&
                player.GetState() != EnumRobotPlaybackState.Playing;
            CanInteract = controller != null && idle;
            CanPlan = CanInteract && pendingPlan == null;
            CanGenerateHulls = pendingDecomposition != null || CanInteract;
            CanEditCollisionLink = pendingDecomposition == null && CanInteract;
            CanEditObstacle = CanInteract && workspace.Collision is { Obstacles.Count: > 0 };
            CanEditPair = CanInteract && PairOwner1Items.Count > 1;
            CanSaveTeachPoints = controller != null;
            CanStopPlayback = player.GetDuration() > 0 || pendingPlan != null ||
                pendingDecomposition != null;
        }

        private void RefreshArmUi()
        {
            var controller = workspace.Controller;
            updatingUi = true;
            try
            {
                ArmItems.Clear();
                if (controller != null)
                {
                    for (uint i = 0; i < controller.ArmCount; ++i)
                    {
                        var name = controller.GetArmName(i);
                        ArmItems.Add("Arm " + (i + 1) + " · " +
                            (string.IsNullOrWhiteSpace(name) ? "Robot" : name));
                    }
                    SelectedArmIndex = Math.Min((int)controller.SelectedArmIndex,
                        Math.Max(0, ArmItems.Count - 1));
                }
            }
            finally
            {
                updatingUi = false;
            }
            RefreshSdkJointCombo();
        }

        partial void OnSelectedArmIndexChanged(int value)
        {
            if (updatingUi || workspace.Controller == null || value < 0)
                return;
            if (!EnsureRobotIdle())
            {
                RefreshArmUi();
                return;
            }
            var armIndex = (uint)value;
            if (!workspace.Controller.SelectArm(armIndex))
            {
                RefreshArmUi();
                SetStatus("无法选择该机械臂。", true);
                return;
            }
            UpdateDefaultTarget();
            SetStatus("已选择 " + workspace.Controller.GetArmName(armIndex));
        }

        private void RefreshSdkJointCombo()
        {
            var controller = workspace.Controller;
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

        private void RefreshJointUi()
        {
            var controller = workspace.Controller;
            var canInteract = controller != null && activeOperation == null && !loadingModel &&
                player.GetState() != EnumRobotPlaybackState.Playing;
            var values = new string[6];
            var enabled = new bool[6];
            for (var i = 0; i < 6; ++i)
            {
                var type = EnumRobotJointType.Fixed;
                var value = double.NaN;
                if (controller != null && i < controller.GetJointCount(controller.SelectedArmIndex))
                {
                    type = controller.GetJointType(controller.SelectedArmIndex, (uint)i);
                    value = controller.GetJointPosition(controller.SelectedArmIndex, (uint)i);
                }
                enabled[i] = type != EnumRobotJointType.Fixed && canInteract;
                if (!double.IsFinite(value) || type == EnumRobotJointType.Fixed)
                {
                    values[i] = "—";
                    continue;
                }
                var display = value;
                var unit = "mm";
                if (type == EnumRobotJointType.Revolute || type == EnumRobotJointType.Continuous)
                {
                    display *= 180.0 / Math.PI;
                    unit = "°";
                }
                values[i] = display.ToString("0.000", CultureInfo.InvariantCulture) + " " + unit;
            }
            JointValues = values;
            JogEnabled = enabled;
            RefreshJogStep();
        }

        private void RefreshJogStep()
        {
            var type = GetStepJointType();
            if (type == null)
            {
                JogStepText = "—";
                return;
            }
            var step = GetJogStep(type.Value);
            var unit = type == EnumRobotJointType.Prismatic ? "mm" : "°";
            JogStepText = step.ToString("0.000", CultureInfo.InvariantCulture) + " " + unit;
        }

        private EnumRobotJointType? GetStepJointType()
        {
            var controller = workspace.Controller;
            if (controller == null)
                return null;
            var count = Math.Min(6, controller.GetJointCount(controller.SelectedArmIndex));
            for (uint i = 0; i < count; ++i)
            {
                var type = controller.GetJointType(controller.SelectedArmIndex, i);
                if (type != EnumRobotJointType.Fixed)
                    return type;
            }
            return null;
        }

        private double GetJogStep(EnumRobotJointType type)
        {
            return type == EnumRobotJointType.Prismatic
                ? Math.Max(0.1, JogStep / 7.0)
                : Math.Max(0.1, JogStep / 35.0);
        }

        private void RefreshPoseUi()
        {
            if (!TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PoseX = Format(x);
            PoseY = Format(y);
            PoseZ = Format(z);
            PoseRx = Format(rx * 180.0 / Math.PI) + "°";
            PoseRy = Format(ry * 180.0 / Math.PI) + "°";
            PoseRz = Format(rz * 180.0 / Math.PI) + "°";
        }

        private void UpdateDefaultTarget()
        {
            if (!TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PlanTargetX = Format(x - 450.0);
            PlanTargetY = Format(y);
            PlanTargetZ = Format(z);
            PlanTargetRx = Format(rx * 180.0 / Math.PI) + "°";
            PlanTargetRy = Format(ry * 180.0 / Math.PI) + "°";
            PlanTargetRz = Format(rz * 180.0 / Math.PI) + "°";
        }

        private void RefreshCollisionUi()
        {
            var model = workspace.Collision;
            var body = workspace.Body;
            var previousIndex = SelectedCollisionLink?.Index ?? 0;
            updatingUi = true;
            try
            {
                CollisionLinkRows.Clear();
                CollisionLinkItems.Clear();
                if (model == null || body == null)
                {
                    CollisionCoverageText = "0%";
                    CollisionCoverageDetail = "尚未加载机器人";
                    CollisionCoverageProgress = 0;
                    CollisionStatusText = "等待加载机器人";
                    CanEditCollisionLink = false;
                    return;
                }
                var total = checked((int)body.GetLinkCount());
                var confirmed = model.LinkHulls.Count;
                var coverage = total == 0 ? 0 : confirmed * 100.0 / total;
                CollisionCoverageText = coverage.ToString("0", CultureInfo.InvariantCulture) + "%";
                CollisionCoverageDetail = confirmed + " / " + total + " 个 Link 已确认";
                CollisionCoverageProgress = coverage;
                for (uint i = 0; i < body.GetLinkCount(); ++i)
                {
                    var count = model.LinkHulls.TryGetValue(i, out var hulls) ? hulls.Count : 0;
                    var name = model.LinkName(i);
                    CollisionLinkItems.Add(new LinkChoice((int)i, name, count));
                    CollisionLinkRows.Add(new LinkRowVm(name, count));
                }
                if (CollisionLinkItems.Count > 0)
                    SelectedCollisionLink = CollisionLinkItems[Math.Clamp(previousIndex, 0,
                        CollisionLinkItems.Count - 1)];
            }
            finally
            {
                updatingUi = false;
            }
            CanEditCollisionLink = pendingDecomposition == null && activeOperation == null &&
                player.GetState() != EnumRobotPlaybackState.Playing;
            CollisionStatusText = candidate == null
                ? "已加载 " + model!.Obstacles.Count + " 个场景实体"
                : "候选凸包待确认";
            RefreshObstacleUi();
            RefreshPairUi();
        }

        partial void OnSelectedCollisionLinkChanged(LinkChoice? value)
        {
            if (updatingUi || value == null)
                return;
            if (candidate != null)
            {
                candidate = null;
                workspace.Collision?.ClearCandidate();
                renderHost.RequestDraw(EnumUpdateFlags.Scene);
            }
            CollisionStatusText = "已选择 " + value.Name;
        }

        private void RefreshObstacleUi()
        {
            var model = workspace.Collision;
            updatingUi = true;
            try
            {
                ObstacleItems.Clear();
                if (model == null)
                {
                    CanEditObstacle = false;
                    return;
                }
                foreach (var item in model.Obstacles)
                    ObstacleItems.Add(new ObstacleItem(item, item.Name));
                if (ObstacleItems.Count > 0)
                {
                    SelectedObstacleItem = ObstacleItems[0];
                    LoadObstacleFields(ObstacleItems[0].Value);
                }
                else
                {
                    ResetObstacleFields();
                    CanEditObstacle = false;
                }
            }
            finally
            {
                updatingUi = false;
            }
        }

        partial void OnSelectedObstacleItemChanged(ObstacleItem? value)
        {
            if (updatingUi || value == null)
                return;
            LoadObstacleFields(value.Value);
        }

        private void LoadObstacleFields(CollisionDemoModel.Obstacle item)
        {
            ObstacleX = Format(item.X);
            ObstacleY = Format(item.Y);
            ObstacleZ = Format(item.Z);
            ObstacleWidth = Format(item.Width);
            ObstacleDepth = Format(item.Depth);
            ObstacleHeight = Format(item.Height);
        }

        private void RefreshPairUi()
        {
            var model = workspace.Collision;
            var body = workspace.Body;
            updatingUi = true;
            try
            {
                PairOwner1Items.Clear();
                PairOwner2Items.Clear();
                IgnoredPairItems.Clear();
                if (model == null || body == null)
                {
                    IgnoredPairCountText = "0 对已忽略";
                    CanEditPair = false;
                    return;
                }
                foreach (var owner in EnumerateOwners(model, body))
                {
                    PairOwner1Items.Add(owner);
                    PairOwner2Items.Add(owner);
                }
                foreach (var pair in model.Ignored.OrderBy(pair => pair.First))
                    IgnoredPairItems.Add(model.OwnerName(pair.First) + " ↔ " +
                        model.OwnerName(pair.Second));
                IgnoredPairCountText = model.Ignored.Count + " 对已忽略";
                CanEditPair = PairOwner1Items.Count > 1;
                if (PairOwner1Items.Count > 0)
                    SelectedOwner1 = PairOwner1Items[0];
                if (PairOwner2Items.Count > 1)
                    SelectedOwner2 = PairOwner2Items[1];
                else if (PairOwner2Items.Count > 0)
                    SelectedOwner2 = PairOwner2Items[0];
            }
            finally
            {
                updatingUi = false;
            }
        }

        private static IEnumerable<OwnerChoice> EnumerateOwners(CollisionDemoModel model, RobotBody body)
        {
            for (uint i = 0; i < body.GetLinkCount(); ++i)
                yield return new OwnerChoice((ulong)i + 1, model.LinkName(i) + " (Link)");
            foreach (var item in model.Obstacles)
                yield return new OwnerChoice(item.Id, item.Name + " (障碍物)");
        }

        private void RefreshPlaybackUi()
        {
            var duration = player.GetDuration();
            var time = player.GetTime();
            var canControl = activeOperation == null && !loadingModel;
            updatingPlayback = true;
            try
            {
                PlaybackMaximum = Math.Max(1.0, duration);
                PlaybackPosition = Math.Clamp(time, 0, Math.Max(1.0, duration));
                CanControlPlayback = duration > 0 && canControl;
                CanStopPlayback = duration > 0 || pendingPlan != null ||
                    pendingDecomposition != null;
                PlaybackTimeText = time.ToString("0.000", CultureInfo.InvariantCulture) + " / " +
                    duration.ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlaybackPlayText = player.GetState() == EnumRobotPlaybackState.Playing ? "暂停" : "播放";
            }
            finally
            {
                updatingPlayback = false;
            }
        }

        partial void OnPlaybackPositionChanged(double value)
        {
            if (updatingPlayback || activeOperation != null || loadingModel ||
                player.GetDuration() <= 0)
                return;
            if (player.Seek(value))
            {
                workspace.Collision?.UpdateLinkTransforms();
                renderHost.RequestDraw(EnumUpdateFlags.Scene);
                RefreshPoseUi();
                RefreshPlaybackUi();
            }
        }

        // ============ 机器人加载 ============

        private void LoadRobot(RobotLoadResult loaded, string label)
        {
            if (loaded?.Body == null || loaded.LoadLinkMesh == null ||
                loaded.Body.GetArmCount() == 0)
            {
                loaded?.Body?.Dispose();
                throw new InvalidOperationException("机器人没有可用的 Arm。");
            }
            loadingModel = true;
            candidate = null;
            ReleaseTargetEditor();
            ResetPlayer();
            ResetPathMetrics();
            SetStatus("正在安装机器人模型…");
            var installed = false;
            try
            {
                workspace.Load(loaded, renderHost.ClearAll);
                installed = true;
                var controller = workspace.Controller!;
                var body = workspace.Body!;
                renderHost.ShowSceneNode(body);
                renderHost.ShowSceneNode(controller.Trajectory);
                renderHost.ShowSceneNode(workspace.CollisionPreview!);
                controller.ShowLinks(IsLinkVisible);
                controller.ShowTracking(IsTrajectoryVisible);
                renderHost.SetStandardView();
                renderHost.ZoomAll();
                modelLabel = label;
                RefreshAllUi();
                UpdateDefaultTarget();
                SetStatus(label + " 已加载");
            }
            catch
            {
                if (installed)
                {
                    try
                    {
                        renderHost.ClearAll();
                    }
                    finally
                    {
                        workspace.Release();
                    }
                }
                throw;
            }
            finally
            {
                loadingModel = false;
            }
        }

        private bool EnsureViewerReady()
        {
            if (viewerReady)
                return true;
            SetStatus("渲染器仍在初始化，请稍候。", true);
            return false;
        }

        private bool EnsureNoActiveOperation()
        {
            if (activeOperation == null)
                return true;
            SetStatus("请先等待或停止当前操作。", true);
            return false;
        }

        private bool EnsureRobotIdle()
        {
            if (activeOperation == null && player.GetState() != EnumRobotPlaybackState.Playing)
                return true;
            SetStatus("轨迹播放或后台操作期间不能修改机器人状态。", true);
            return false;
        }

        // ============ 命令：场景资源折叠 ============

        [RelayCommand]
        private void ToggleSceneTree() => SceneTreeExpanded = !SceneTreeExpanded;

        // ============ 命令：加载 / 装配 ============

        [RelayCommand]
        private void ImportUrdf()
        {
            if (!EnsureViewerReady() || !EnsureNoActiveOperation())
                return;
            var dialog = new OpenFileDialog {
                Filter = "URDF robot model (*.urdf)|*.urdf",
                Title = "Open robot URDF",
                InitialDirectory = GetModelDirectory(null)
            };
            if (dialog.ShowDialog() != true)
                return;
            var urdfFile = dialog.FileName;
            try
            {
                RobotBody body;
                using (var path = new AnyCAD.Foundation.Path(urdfFile))
                    body = RobotIO.Load(path);
                LoadRobot(new RobotLoadResult(body,
                    name => UrdfVisualMesh.Load(urdfFile, name)),
                    IOPath.GetFileNameWithoutExtension(urdfFile));
            }
            catch (Exception error)
            {
                SetStatus("URDF 加载失败：" + error.Message, true);
            }
        }

        [RelayCommand]
        private void LoadManualKr500()
        {
            if (!EnsureViewerReady() || !EnsureNoActiveOperation())
                return;
            var directory = GetModelDirectory("Kr500");
            try
            {
                var result = new RobotLoadResult(
                    ManualKr500Factory.Create(directory),
                    name => ManualKr500Factory.LoadLinkMesh(directory, name));
                LoadRobot(result, "Kr500");
            }
            catch (Exception error)
            {
                SetStatus("Kr500 创建失败：" + error.Message, true);
            }
        }

        [RelayCommand]
        private void LoadManual3A()
        {
            if (!EnsureViewerReady() || !EnsureNoActiveOperation())
                return;
            var directory = GetModelDirectory("3A");
            try
            {
                var result = new RobotLoadResult(
                    Manual3AFactory.Create(directory),
                    name => Manual3AFactory.LoadLinkMesh(directory, name));
                LoadRobot(result, "3A");
            }
            catch (Exception error)
            {
                SetStatus("3A 创建失败：" + error.Message, true);
            }
        }

        private string GetModelDirectory(string? child)
        {
            var directory = IOPath.Combine(AppContext.BaseDirectory, "Model");
            return child == null ? directory : IOPath.Combine(directory, child);
        }

        // ============ 命令：示教 ============

        private void JogJoint(int index, double direction)
        {
            if (!EnsureRobotIdle())
                return;
            var controller = workspace.Controller;
            if (controller == null || index < 0 ||
                index >= controller.GetJointCount(controller.SelectedArmIndex))
                return;
            var type = controller.GetJointType(controller.SelectedArmIndex, (uint)index);
            if (type == EnumRobotJointType.Fixed)
                return;
            var step = direction * GetJogStep(type);
            if (type == EnumRobotJointType.Revolute || type == EnumRobotJointType.Continuous)
                step *= Math.PI / 180.0;
            if (!controller.AddJointPosition(controller.SelectedArmIndex, (uint)index, step))
                SetStatus("关节点动失败或已达到限位。", true);
        }

        [RelayCommand]
        private void ApplyPose()
        {
            if (!EnsureRobotIdle())
                return;
            var controller = workspace.Controller;
            if (controller == null)
                return;
            if (!TryReadPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
            {
                SetStatus("末端位姿包含无效数字。", true);
                return;
            }
            using var target = RobotAssemblyPose.Create(x, y, z,
                rx * Math.PI / 180.0, ry * Math.PI / 180.0, rz * Math.PI / 180.0);
            if (!controller.TryMoveSelectedArmTip(target))
                SetStatus("末端 IK 失败，请检查目标可达性。", true);
            else
                SetStatus("末端位姿已应用");
        }

        [RelayCommand]
        private void RecordPoint()
        {
            var controller = workspace.Controller;
            if (controller == null || !TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            var joints = new double[6];
            for (var i = 0; i < 6; ++i)
                joints[i] = i < controller.GetJointCount(controller.SelectedArmIndex)
                    ? controller.GetJointPosition(controller.SelectedArmIndex, (uint)i) : 0;
            teachPoints.Add(new TeachPoint(DateTimeOffset.Now,
                new[] { x, y, z }, new[] { rx, ry, rz }, joints));
            using var point = new Vector3((float)x, (float)y, (float)z);
            controller.AddTrackingPoint(point);
            SetStatus("已记录示教点 " + teachPoints.Count);
        }

        [RelayCommand]
        private void SaveTeachPoints()
        {
            var dialog = new SaveFileDialog {
                Filter = "AnyRobot teach points (*.json)|*.json",
                FileName = "teach-points.json"
            };
            if (dialog.ShowDialog() != true)
                return;
            try
            {
                File.WriteAllText(dialog.FileName,
                    JsonSerializer.Serialize(teachPoints, new JsonSerializerOptions { WriteIndented = true }));
                SetStatus("已保存 " + teachPoints.Count + " 个示教点");
            }
            catch (Exception error)
            {
                SetStatus("示教点保存失败：" + error.Message, true);
            }
        }

        [RelayCommand]
        private void ResetRobot()
        {
            if (!EnsureRobotIdle())
                return;
            workspace.Controller?.Reset();
            SetStatus("机器人已回到 Home");
        }

        [RelayCommand]
        private async Task EmergencyStopAsync()
        {
            try
            {
                pendingPlan?.Cancel();
                pendingDecomposition?.Cancel();
                player.Stop();
                playbackClock.Reset();
                RefreshPlaybackUi();
                var operation = activeOperation;
                if (operation != null)
                    await operation;
                if (closing)
                    return;
                workspace.Controller?.Reset();
                SetStatus("急停：后台操作已取消，机器人已停止并回到 Home", true);
            }
            catch (Exception error)
            {
                if (!closing)
                    SetStatus("急停失败：" + error.Message, true);
            }
        }

        // ============ 命令：视图 ============

        [RelayCommand]
        private void FitView()
        {
            if (viewerReady && workspace.Body != null)
                renderHost.ZoomAll();
        }

        partial void OnIsLinkVisibleChanged(bool value)
        {
            if (!updatingUi)
                workspace.Controller?.ShowLinks(value);
        }

        partial void OnIsTrajectoryVisibleChanged(bool value)
        {
            if (!updatingUi)
                workspace.Controller?.ShowTracking(value);
        }

        partial void OnIsGridVisibleChanged(bool value)
        {
            if (!updatingUi && viewerReady)
                renderHost.ShowCoordinateGrid(value);
        }

        // ============ 命令：交互式目标 ============

        [RelayCommand]
        private void ToggleInteractiveTarget()
        {
            if (targetEditor != null)
            {
                ReleaseTargetEditor();
                SetStatus("交互式目标编辑器已释放。");
                return;
            }
            var controller = workspace.Controller;
            if (controller == null)
            {
                SetStatus("请先加载机器人。", true);
                return;
            }
            if (!EnsureRobotIdle())
                return;
            using var transform = controller.GetArmTipWorldTransform(controller.SelectedArmIndex);
            using var tipPoint = Vector3d.GetPosition(transform);
            if (transform == null || !double.IsFinite(tipPoint.x) ||
                !double.IsFinite(tipPoint.y) || !double.IsFinite(tipPoint.z))
            {
                SetStatus("当前末端位姿无效，无法创建交互式目标。", true);
                return;
            }
            targetNode = new GroupSceneNode();
            targetNode.SetName("IK target");
            using var geometry = GeometryBuilder.CreateSphere(18.0f, 16, 16);
            var marker = PrimitiveSceneNode.Create(geometry, null);
            using var color = new Vector3(0.30f, 0.85f, 1.0f);
            marker.SetColor(color);
            marker.SetPickable(false);
            targetNode.AddNode(marker);
            targetNode.SetTransform(transform);
            renderHost.ShowSceneNode(targetNode);
            targetEditor = new RobotTargetEditor(targetNode, controller);
            renderHost.SetEditor(targetEditor);
            SetStatus("交互式目标已启用：拖动坐标框移动末端，机器人实时跟随。");
        }

        private void ReleaseTargetEditor()
        {
            if (targetEditor == null && targetNode == null)
                return;
            renderHost.ClearEditor();
            if (targetNode != null)
            {
                try
                {
                    targetNode.SetVisible(false);
                }
                catch
                {
                    // The node may already have been disposed by ClearAll on robot reload.
                }
            }
            targetEditor = null;
            targetNode = null;
            if (viewerReady)
                renderHost.RequestDraw(EnumUpdateFlags.Scene);
        }

        // ============ 命令：碰撞检查 ============

        [RelayCommand]
        private async Task GenerateHullsAsync()
        {
            if (pendingDecomposition != null)
            {
                pendingDecomposition.Cancel();
                CollisionStatusText = "正在取消凸分解…";
                return;
            }
            if (!EnsureNoActiveOperation())
                return;
            await RunTrackedAsync(GenerateHullsCore);
        }

        private async Task GenerateHullsCore()
        {
            var model = workspace.Collision;
            var body = workspace.Body;
            if (model == null || body == null || workspace.LoadLinkMesh == null ||
                SelectedCollisionLink is not { } selected)
            {
                CollisionStatusText = "请先加载机器人并选择 Link";
                return;
            }
            var linkIndex = checked((uint)selected.Index);
            candidate = null;
            model.ClearCandidate();
            try
            {
                using var mesh = workspace.LoadLinkMesh(model.LinkName(linkIndex));
                if (!mesh.IsValid())
                    throw new InvalidOperationException("Link 碰撞网格无效。");
                using var options = new VhacdOptions {
                    MaxConvexHulls = 8,
                    Resolution = 100000,
                    MinimumVolumePercentErrorAllowed = 2.5,
                    MaxRecursionDepth = 8,
                    MaxNumVerticesPerCH = 32
                };
                if (!options.IsValid())
                    throw new InvalidOperationException("V-HACD 参数无效。");
                if (!ConvexDecompositionSession.IsAvailable())
                    throw new InvalidOperationException("V-HACD 后端不可用。");
                using var session = new ConvexDecompositionSession(mesh, options);
                pendingDecomposition = session;
                GenerateHullsContent = "取消生成";
                CollisionStatusText = "正在生成凸包…";
                using var result = await Task.Run(session.Compute);
                if (closing)
                    return;
                if (!result.Succeeded())
                    throw new InvalidOperationException(result.State + ": " + result.Diagnostic);
                var hulls = new List<CollisionDemoModel.Hull>();
                using (var source = result.Hulls)
                {
                    foreach (var hull in source)
                    {
                        using (hull)
                        using (var vertices = hull.Vertices)
                        using (var indices = hull.Indices)
                        {
                            var coordinates = new double[vertices.Count * 3];
                            for (var i = 0; i < vertices.Count; ++i)
                            {
                                using var point = vertices[i];
                                coordinates[3 * i] = point.x;
                                coordinates[3 * i + 1] = point.y;
                                coordinates[3 * i + 2] = point.z;
                            }
                            if (coordinates.Any(value => !double.IsFinite(value)))
                                throw new InvalidOperationException("V-HACD 返回了无效顶点。");
                            var indexValues = indices.ToArray();
                            if (indexValues.Any(index => index >= vertices.Count))
                                throw new InvalidOperationException("V-HACD 返回了无效索引。");
                            hulls.Add(new CollisionDemoModel.Hull(coordinates, indexValues));
                        }
                    }
                }
                if (hulls.Count == 0)
                    throw new InvalidOperationException("V-HACD 没有返回凸包。");
                candidate = (linkIndex, hulls);
                model.SetCandidate(linkIndex, hulls);
                CollisionStatusText = "候选预览已生成，共 " + hulls.Count + " 个凸包，请确认。";
                RefreshCollisionUi();
                renderHost.RequestDraw(EnumUpdateFlags.Scene);
            }
            catch (Exception error)
            {
                if (!closing)
                    CollisionStatusText = "凸分解失败：" + error.Message;
            }
            finally
            {
                pendingDecomposition = null;
                GenerateHullsContent = "生成 / 重新生成";
                RefreshInteractionState();
            }
        }

        [RelayCommand]
        private void ConfirmHulls()
        {
            if (!EnsureRobotIdle())
                return;
            if (workspace.Collision == null || candidate is not { } next)
            {
                CollisionStatusText = "请先生成候选凸包。";
                return;
            }
            workspace.Collision.SetLinkHulls(next.Index, next.Hulls);
            candidate = null;
            workspace.Collision.ClearCandidate();
            RefreshCollisionUi();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已确认 " + next.Hulls.Count + " 个凸包。";
        }

        [RelayCommand]
        private void DeleteLinkHulls()
        {
            if (!EnsureRobotIdle())
                return;
            var model = workspace.Collision;
            if (model == null || SelectedCollisionLink is not { } selected)
                return;
            var linkIndex = (uint)selected.Index;
            if (!model.LinkHulls.ContainsKey(linkIndex))
            {
                CollisionStatusText = "该 Link 尚无已确认凸包。";
                return;
            }
            candidate = null;
            model.ClearCandidate();
            model.RemoveLinkHulls(linkIndex);
            RefreshCollisionUi();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已删除 Link 凸包：" + model.LinkName(linkIndex);
        }

        [RelayCommand]
        private void AddObstacle()
        {
            if (!EnsureRobotIdle())
                return;
            var model = workspace.Collision;
            if (model == null)
            {
                CollisionStatusText = "请先加载机器人。";
                return;
            }
            model.AddObstacle();
            RefreshCollisionUi();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已添加场景障碍物。";
        }

        [RelayCommand]
        private void UpdateObstacle()
        {
            if (!EnsureRobotIdle())
                return;
            var model = workspace.Collision;
            if (model == null || SelectedObstacleItem?.Value is not { } item)
            {
                CollisionStatusText = "请先添加障碍物。";
                return;
            }
            if (!TryReadObstacle(out var x, out var y, out var z,
                    out var w, out var d, out var h) || w <= 0 || d <= 0 || h <= 0)
            {
                CollisionStatusText = "障碍物尺寸必须大于 0。";
                return;
            }
            item.X = x; item.Y = y; item.Z = z;
            item.Width = w; item.Depth = d; item.Height = h;
            model.RebuildPreview();
            RefreshObstacleUi();
            RefreshPairUi();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "障碍物已更新：" + item.Name;
        }

        [RelayCommand]
        private void DeleteObstacle()
        {
            if (!EnsureRobotIdle())
                return;
            var model = workspace.Collision;
            if (model == null || SelectedObstacleItem?.Value is not { } item)
                return;
            model.RemoveObstacle(item);
            RefreshCollisionUi();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已删除障碍物：" + item.Name;
        }

        [RelayCommand]
        private void ResetObstacleFields()
        {
            ResetObstacleFieldsCore();
        }

        private void ResetObstacleFieldsCore()
        {
            ObstacleX = "2750";
            ObstacleY = "0";
            ObstacleZ = "1470";
            ObstacleWidth = "300";
            ObstacleDepth = "300";
            ObstacleHeight = "400";
        }

        private void SetPairIgnored(bool value)
        {
            var model = workspace.Collision;
            if (model == null || SelectedOwner1 is not { } first ||
                SelectedOwner2 is not { } second)
                return;
            if (first.Id == second.Id)
            {
                CollisionStatusText = "同一实体不能组成豁免对。";
                return;
            }
            var changed = model.SetIgnored(first.Id, second.Id, value);
            var message = value ? "已忽略" : "已恢复检查";
            if (!changed)
                message = value ? "已在忽略列表" : "不在忽略列表";
            CollisionStatusText = message + "：" + model.OwnerName(first.Id) +
                " ↔ " + model.OwnerName(second.Id);
            RefreshPairUi();
        }

        [RelayCommand]
        private async Task RunCollisionCheckAsync()
        {
            if (!EnsureNoActiveOperation())
                return;
            await RunTrackedAsync(CollisionCheckCore);
        }

        private async Task CollisionCheckCore()
        {
            var body = workspace.Body;
            var model = workspace.Collision;
            if (body == null || model == null)
            {
                CollisionStatusText = "请先加载机器人。";
                return;
            }
            if (model.LinkHulls.Count == 0)
            {
                CollisionStatusText = "请先生成并确认至少一个 Link 凸包。";
                return;
            }
            ResetPlayer();
            CanPlan = false;
            try
            {
                using var session = new RobotSimulationSession(body, backends);
                pendingPlan = session;
                if (!session.IsValid())
                    throw new InvalidOperationException(session.GetDiagnostic());
                using var goals = new RobotJointPositionList();
                using var limits = new RobotMotionLimitList();
                AddCurrentJointGoals(body, goals, limits);
                using var request = session.CreatePlanningRequest(goals, limits);
                using (var options = request.Options)
                    options.CollisionResolution = 0.05;
                model.ApplyTo(request);
                CollisionStatusText = "正在检查当前姿态…";
                using var result = await Task.Run(() => session.Plan(request));
                if (closing)
                    return;
                var modeled = model.LinkHulls.Count;
                var total = body.GetLinkCount();
                if (result.Succeeded())
                    CollisionStatusText = modeled == total
                        ? "全部 Link 已建模，当前姿态未检测到碰撞。"
                        : "已建模的 " + modeled + " / " + total +
                          " 个 Link 未检测到碰撞；未建模 Link 未检查。";
                else
                {
                    using var planning = result.Planning;
                    CollisionStatusText = "当前姿态发生碰撞：" + planning.Status + " " + planning.Diagnostic;
                }
            }
            catch (Exception error)
            {
                if (!closing)
                    CollisionStatusText = "碰撞检查失败：" + error.Message;
            }
            finally
            {
                pendingPlan = null;
                CanPlan = true;
            }
        }

        // ============ 命令：路径规划 ============

        [RelayCommand]
        private void UseCurrentPose()
        {
            if (!TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PlanTargetX = Format(x);
            PlanTargetY = Format(y);
            PlanTargetZ = Format(z);
            PlanTargetRx = Format(rx * 180.0 / Math.PI) + "°";
            PlanTargetRy = Format(ry * 180.0 / Math.PI) + "°";
            PlanTargetRz = Format(rz * 180.0 / Math.PI) + "°";
            SetStatus("目标位姿已复制当前末端姿态");
        }

        [RelayCommand]
        private async Task PlanPathAsync()
        {
            if (!EnsureNoActiveOperation())
                return;
            await RunTrackedAsync(PlanPathCore);
        }

        private async Task PlanPathCore()
        {
            var body = workspace.Body;
            var model = workspace.Collision;
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
            if (!TryParseNumber(CollisionResolution, out var collisionResolution) ||
                collisionResolution <= 0)
            {
                PlanStatusText = "碰撞采样间隔必须是大于 0 的有限数值。";
                return;
            }
            if (!TryParseNumber(PlanTimeout, out var timeoutSeconds) ||
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
            ResetPlayer();
            CanPlan = false;
            PlanStatusText = "正在规划无碰撞路径…";
            try
            {
                using var session = new RobotSimulationSession(body, backends);
                pendingPlan = session;
                if (!session.IsValid())
                    throw new InvalidOperationException(session.GetDiagnostic());
                var controller = workspace.Controller ??
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
                if (closing)
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
                if (!player.Load(body, result))
                    throw new InvalidOperationException(player.GetDiagnostic());
                RefreshPlaybackUi();
                PathDurationText = player.GetDuration().ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlanStatusText = "无碰撞路径已就绪";
                PlanBackendText = backendText;
            }
            catch (Exception error)
            {
                if (!closing)
                    PlanStatusText = "路径规划失败：" + error.Message;
            }
            finally
            {
                pendingPlan = null;
                CanPlan = true;
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

        private void AddCurrentJointGoals(RobotBody body, RobotJointPositionList goals,
            RobotMotionLimitList limits)
        {
            for (uint i = 0; i < body.GetJointCount(); ++i)
            {
                using var jointIndex = new RobotJointIndex(i);
                using var joint = body.GetJoint(jointIndex);
                if (joint.GetJointType() == EnumRobotJointType.Fixed)
                    continue;
                using var position = new RobotJointPosition {
                    JointIndex = jointIndex,
                    Position = body.GetJointPosition(jointIndex)
                };
                goals.Add(position);
                var velocity = joint.GetMaxVelocity() > 0 ? joint.GetMaxVelocity() : 1;
                using var limit = new RobotMotionLimit {
                    JointIndex = jointIndex,
                    MaxVelocity = velocity,
                    MaxAcceleration = 2 * velocity
                };
                limits.Add(limit);
            }
        }

        // ============ 后端选择 ============

        private void InitializeBackends()
        {
            FillBackendCombo(IkBackendItems, EnumSimulationBackendKind.Ik, backends.Ik);
            FillBackendCombo(PlannerItems, EnumSimulationBackendKind.Planner, backends.Planner);
            FillBackendCombo(CollisionBackendItems, EnumSimulationBackendKind.Collision, backends.Collision);
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
                backends.Ik = ik;
                backends.Planner = planner;
                backends.Collision = collision;
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
                SetStatus("后端设置失败：" + error.Message, true);
            }
        }

        // ============ SDK 单关节规划 / IK 检查 ============

        partial void OnSelectedSdkJointChanged(JointChoice? value)
        {
            if (updatingUi || value == null)
                return;
            LoadSdkJointDefaults();
        }

        private void LoadSdkJointDefaults()
        {
            var body = workspace.Body;
            if (body == null || SelectedSdkJoint is not { } choice)
                return;
            using var index = new RobotJointIndex(choice.Index);
            using var joint = body.GetJoint(index);
            SdkGoal = body.GetJointPosition(index).ToString("0.0000", CultureInfo.InvariantCulture);
            var velocity = joint.GetMaxVelocity() > 0 ? joint.GetMaxVelocity() : 1;
            SdkVelocity = velocity.ToString("0.000", CultureInfo.InvariantCulture);
            SdkAcceleration = (2 * velocity).ToString("0.000", CultureInfo.InvariantCulture);
        }

        [RelayCommand]
        private async Task PlanSingleJointAsync()
        {
            var body = workspace.Body;
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
            if (!TryParseNumber(SdkGoal, out var goalValue) ||
                !TryParseNumber(SdkVelocity, out var velocityValue) ||
                !TryParseNumber(SdkAcceleration, out var accelerationValue) ||
                velocityValue <= 0 || accelerationValue <= 0)
            {
                PlanStatusText = "速度与加速度必须大于 0，目标值必须是有效数字。";
                return;
            }
            if (!EnsureNoActiveOperation())
                return;
            ResetPlayer();
            CanPlan = false;
            PlanStatusText = "正在规划单关节轨迹…";
            try
            {
                using var session = new RobotSimulationSession(body, backends);
                pendingPlan = session;
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
                if (closing)
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
                if (!player.Load(body, result))
                    throw new InvalidOperationException(player.GetDiagnostic());
                RefreshPlaybackUi();
                PathDurationText = player.GetDuration().ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlanStatusText = "单关节轨迹已就绪，点击播放开始演示。";
                PlanBackendText = backends.Planner + " / " + backends.Collision;
            }
            catch (Exception error)
            {
                if (!closing)
                    PlanStatusText = "单关节规划失败：" + error.Message;
            }
            finally
            {
                pendingPlan = null;
                CanPlan = true;
            }
        }

        [RelayCommand]
        private void CheckAllTipIk()
        {
            var body = workspace.Body;
            if (body == null)
            {
                PlanStatusText = "请先加载机器人。";
                return;
            }
            var report = new StringBuilder();
            try
            {
                using var session = new RobotSimulationSession(body, backends);
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
                SdkCheckIkStatusText = "IK 后端：" + backends.Ik;
            }
            catch (Exception error)
            {
                if (!closing)
                    PlanStatusText = "IK 检查失败：" + error.Message;
            }
        }

        // ============ 播放 ============

        [RelayCommand]
        private void PlaybackRestart()
        {
            if (!EnsureRobotIdle())
                return;
            if (player.GetDuration() <= 0)
                return;
            StopPlaybackCore();
            SetStatus("轨迹已回到起点");
        }

        [RelayCommand]
        private void PlaybackPlay()
        {
            if (!EnsureRobotIdle())
                return;
            if (player.GetDuration() <= 0)
            {
                SetStatus("请先计算一条路径。", true);
                return;
            }
            if (player.GetState() == EnumRobotPlaybackState.Playing)
            {
                player.Pause();
                playbackClock.Reset();
                SetStatus("轨迹已暂停");
            }
            else
            {
                if (player.GetTime() >= player.GetDuration() - 0.001)
                    StopPlaybackCore();
                if (player.Play())
                {
                    playbackClock.Restart();
                    SetStatus("轨迹播放中");
                }
                else
                    SetStatus("轨迹播放失败：" + player.GetDiagnostic(), true);
            }
            RefreshInteractionState();
        }

        [RelayCommand]
        private void PlaybackStop()
        {
            if (pendingPlan != null || pendingDecomposition != null)
            {
                pendingPlan?.Cancel();
                pendingDecomposition?.Cancel();
                SetStatus("正在取消当前操作…");
            }
            StopPlaybackCore();
        }

        private void StopPlaybackCore()
        {
            player.Stop();
            playbackClock.Reset();
            workspace.Collision?.UpdateLinkTransforms();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            RefreshPoseUi();
            RefreshPlaybackUi();
        }

        private void ResetPlayer()
        {
            var previous = player;
            player = new RobotTrajectoryPlayer();
            try
            {
                previous.Stop();
            }
            finally
            {
                previous.Dispose();
            }
            playbackClock.Reset();
            ResetPathMetrics();
            RefreshPlaybackUi();
        }

        private void ResetPathMetrics()
        {
            PathLengthText = "—";
            PathDurationText = "—";
            PathSamplesText = "—";
        }

        /// <summary>由 View 的渲染定时器每帧调用。</summary>
        public void OnPlaybackTick()
        {
            if (pendingDecomposition != null)
            {
                var progress = pendingDecomposition.GetProgress();
                CollisionStatusText = "正在生成凸包 " +
                    progress.ToString("0", CultureInfo.InvariantCulture) + "%";
                DecomposeProgress = Math.Clamp(progress * 100, 0, 100);
            }
            else if (DecomposeProgress != 0)
            {
                DecomposeProgress = 0;
            }
            if (closing || activeOperation != null ||
                player.GetState() != EnumRobotPlaybackState.Playing)
                return;
            var elapsed = playbackClock.Elapsed.TotalSeconds;
            playbackClock.Restart();
            var advanced = player.Tick(elapsed);
            if (!advanced)
            {
                playbackClock.Reset();
                SetStatus("轨迹播放结束：" + player.GetDiagnostic());
            }
            workspace.Collision?.UpdateLinkTransforms();
            renderHost.RequestDraw(EnumUpdateFlags.Scene);
            RefreshPoseUi();
            RefreshPlaybackUi();
            if (!advanced)
                RefreshInteractionState();
        }

        // ============ 通用 ============

        private async Task RunTrackedAsync(Func<Task> operation)
        {
            Task? task = null;
            try
            {
                task = operation();
                activeOperation = task;
                RefreshInteractionState();
                await task;
            }
            catch (OperationCanceledException)
            {
                if (!closing)
                    SetStatus("当前操作已取消。");
            }
            catch (Exception error)
            {
                if (!closing)
                    SetStatus("操作失败：" + error.Message, true);
            }
            finally
            {
                if (ReferenceEquals(activeOperation, task))
                    activeOperation = null;
                if (!closing)
                    RefreshInteractionState();
            }
        }

        private void SetStatus(string message, bool error = false)
        {
            GlobalStatusText = message;
            GlobalStatusBrush = error
                ? Brushes.Firebrick
                : new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }

        private bool TryGetCurrentPose(out double x, out double y, out double z,
            out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            var controller = workspace.Controller;
            if (controller == null)
                return false;
            using var transform = controller.GetArmTipWorldTransform(controller.SelectedArmIndex);
            using var point = Vector3d.GetPosition(transform);
            using var euler = transform.extractEulerAngleXYZ();
            x = point.x;
            y = point.y;
            z = point.z;
            rx = euler.x;
            ry = euler.y;
            rz = euler.z;
            return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) &&
                double.IsFinite(rx) && double.IsFinite(ry) && double.IsFinite(rz);
        }

        private bool TryReadPose(out double x, out double y, out double z,
            out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            return TryParseNumber(PoseX, out x) && TryParseNumber(PoseY, out y) &&
                TryParseNumber(PoseZ, out z) && TryParseNumber(PoseRx, out rx) &&
                TryParseNumber(PoseRy, out ry) && TryParseNumber(PoseRz, out rz);
        }

        private bool TryReadPlanTarget(out double x, out double y, out double z,
            out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            return TryParseNumber(PlanTargetX, out x) && TryParseNumber(PlanTargetY, out y) &&
                TryParseNumber(PlanTargetZ, out z) && TryParseNumber(PlanTargetRx, out rx) &&
                TryParseNumber(PlanTargetRy, out ry) && TryParseNumber(PlanTargetRz, out rz);
        }

        private bool TryReadObstacle(out double x, out double y, out double z,
            out double w, out double d, out double h)
        {
            x = y = z = w = d = h = 0;
            return TryParseNumber(ObstacleX, out x) &&
                TryParseNumber(ObstacleY, out y) &&
                TryParseNumber(ObstacleZ, out z) &&
                TryParseNumber(ObstacleWidth, out w) &&
                TryParseNumber(ObstacleDepth, out d) &&
                TryParseNumber(ObstacleHeight, out h);
        }

        private static bool TryParseNumber(string text, out double value)
        {
            var normalized = (text ?? string.Empty).Replace("−", "-").Replace("°", string.Empty)
                .Replace(",", string.Empty).Trim();
            var token = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? normalized;
            return double.TryParse(token, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }

        private static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
