using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace RapidRobot.Wpf.ViewModels
{
    /// <summary>
    /// 根 ViewModel：窗口级状态、渲染/生命周期、机器人加载、播放器、
    /// 互斥状态计算，并组合三个 Tab 子 ViewModel（示教/碰撞检查/路径规划）。
    /// 共享依赖（workspace/backends/renderHost/player/pending 会话）全部留在此处，
    /// 子 VM 通过本类的公开成员访问，保证互斥与释放逻辑单一来源。
    /// </summary>
    public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        // ---- 共享依赖（子 VM 经公开属性访问） ----

        private readonly RobotWorkspaceSession workspace = new();
        private readonly BackendSelection backends;
        private readonly IRenderHost renderHost;
        private readonly Stopwatch playbackClock = new();
        private RobotTrajectoryPlayer player = new();
        private RobotSimulationSession? pendingPlan;
        private ConvexDecompositionSession? pendingDecomposition;
        private Task? activeOperation;
        private bool viewerReady;
        private bool loadingModel;
        private bool updatingPlayback;
        private bool closing;
        private bool shutdownComplete;
        private string modelLabel = "未加载";
        private RobotTargetEditor? targetEditor;
        private GroupSceneNode? targetNode;

        internal RobotWorkspaceSession Workspace => workspace;
        internal BackendSelection Backends => backends;
        internal IRenderHost RenderHost => renderHost;
        internal RobotTrajectoryPlayer Player => player;
        internal RobotSimulationSession? PendingPlan { get => pendingPlan; set => pendingPlan = value; }
        internal ConvexDecompositionSession? PendingDecomposition
        {
            get => pendingDecomposition;
            set => pendingDecomposition = value;
        }
        internal Task? ActiveOperation => activeOperation;
        internal bool IsLoadingModel => loadingModel;
        internal bool IsClosing => closing;

        // ---- 子 ViewModel（Tab） ----

        public TeachViewModel TeachTab { get; }
        public CollisionViewModel CollisionTab { get; }
        public PlanViewModel PlanTab { get; }

        // ---- observable UI state（窗口级） ----

        [ObservableProperty]
        private string sceneResourceName = "未加载";

        [ObservableProperty]
        private string sceneResourceStatus = "未加载";

        // 场景资源目录树：当前模型的全部 Link 名称（随加载场景动态更新）
        public ObservableCollection<string> SceneTreeItems { get; } = new();

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

        // 末端位姿（起点/播放实时刷新）
        [ObservableProperty]
        private string tipPoseText = "—";

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

        public MainWindowViewModel(IRenderHost host)
        {
            renderHost = host;
            backends = SimulationBackends.GetDefaults();
            workspace.Changed += OnWorkspaceChanged;
            TeachTab = new TeachViewModel(this);
            CollisionTab = new CollisionViewModel(this);
            PlanTab = new PlanViewModel(this);
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
            SceneTreeItems.Clear();
            var collision = workspace.Collision;
            if (collision != null && body != null)
            {
                for (uint i = 0; i < body.GetLinkCount(); ++i)
                    SceneTreeItems.Add(collision.LinkName(i));
            }
            ViewerEmptyVisibility = body == null ? Visibility.Visible : Visibility.Collapsed;
            RobotConnectionBrush = body == null
                ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
                : new SolidColorBrush(Color.FromRgb(34, 197, 94));
            RobotDofText = controller == null
                ? "未加载"
                : modelLabel + " · " + controller.ArmCount + " ARM";
            GlobalStatusText = body == null ? "等待加载机器人模型" : "安全状态：可操作";
            RefreshInteractionState();
            TeachTab.RefreshAllUi();
            CollisionTab.RefreshAllUi();
            PlanTab.RefreshAllUi();
            RefreshPlaybackUi();
            RefreshTipPose();
        }

        public void RefreshInteractionState()
        {
            var controller = workspace.Controller;
            var idle = activeOperation == null && !loadingModel &&
                player.GetState() != EnumRobotPlaybackState.Playing;
            CanInteract = controller != null && idle;
            CanPlan = CanInteract && pendingPlan == null;
            CanGenerateHulls = pendingDecomposition != null || CanInteract;
            CanEditCollisionLink = pendingDecomposition == null && CanInteract;
            CanEditObstacle = CanInteract && workspace.Collision is { Obstacles.Count: > 0 };
            CanEditPair = CanInteract && CollisionTab.PairOwner1Items.Count > 1;
            CanSaveTeachPoints = controller != null;
            CanStopPlayback = player.GetDuration() > 0 || pendingPlan != null ||
                pendingDecomposition != null;
        }

        public void RefreshPlaybackUi()
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
                TeachTab.RefreshPoseUi();
                RefreshTipPose();
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
            ReleaseTargetEditor();
            ResetPlayer();
            PlanTab.ResetPathMetrics();
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
                if (label == "Kr500")
                    SetKr500DemoPose(body);
                modelLabel = label;
                RefreshAllUi();
                PlanTab.UpdateDefaultTarget();
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

        public bool EnsureNoActiveOperation()
        {
            if (activeOperation == null)
                return true;
            SetStatus("请先等待或停止当前操作。", true);
            return false;
        }

        public bool EnsureRobotIdle()
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

        private static void SetKr500DemoPose(RobotBody body)
        {
            try
            {
                using var axis2 = new RobotJointIndex(1);
                using var axis3 = new RobotJointIndex(2);
                body.SetJointPosition(axis2, -0.45);
                body.SetJointPosition(axis3, 0.6);
                body.UpdateFrames();
            }
            catch
            {
                // 演示姿态仅为可视化优化，失败不影响加载。
            }
        }

        private string GetModelDirectory(string? child)
        {
            var directory = IOPath.Combine(AppContext.BaseDirectory, "Model");
            return child == null ? directory : IOPath.Combine(directory, child);
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
            workspace.Controller?.ShowLinks(value);
        }

        partial void OnIsTrajectoryVisibleChanged(bool value)
        {
            workspace.Controller?.ShowTracking(value);
        }

        partial void OnIsGridVisibleChanged(bool value)
        {
            if (viewerReady)
                renderHost.ShowCoordinateGrid(value);
        }

        // ============ 命令：急停 ============

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
            TeachTab.RefreshPoseUi();
            RefreshTipPose();
            RefreshPlaybackUi();
        }

        public void ResetPlayer()
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
            PlanTab.ResetPathMetrics();
            RefreshPlaybackUi();
        }

        /// <summary>由 View 的渲染定时器每帧调用。</summary>
        public void OnPlaybackTick()
        {
            CollisionTab.UpdateDecomposeProgress();
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
            TeachTab.RefreshPoseUi();
            RefreshTipPose();
            RefreshPlaybackUi();
            if (!advanced)
                RefreshInteractionState();
        }

        // ============ 通用 ============

        public async Task RunTrackedAsync(Func<Task> operation)
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

        public void SetStatus(string message, bool error = false)
        {
            GlobalStatusText = message;
            GlobalStatusBrush = error
                ? Brushes.Firebrick
                : new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }

        public bool TryGetCurrentPose(out double x, out double y, out double z,
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

        public void RefreshTipPose()
        {
            if (!TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
            {
                TipPoseText = "—";
                return;
            }
            TipPoseText = "X " + Format(x) + "   Y " + Format(y) + "   Z " + Format(z) +
                "\nRX " + Format(rx * 180.0 / Math.PI) + "°   RY " +
                Format(ry * 180.0 / Math.PI) + "°   RZ " + Format(rz * 180.0 / Math.PI) + "°";
        }

        internal static bool TryParseNumber(string text, out double value)
        {
            var normalized = (text ?? string.Empty).Replace("−", "-").Replace("°", string.Empty)
                .Replace(",", string.Empty).Trim();
            var token = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? normalized;
            return double.TryParse(token, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }

        internal static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
