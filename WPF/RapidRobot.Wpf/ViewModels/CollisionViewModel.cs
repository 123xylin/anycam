using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace RapidRobot.Wpf.ViewModels
{
    /// <summary>
    /// 碰撞检查 Tab：Link 凸包（V-HACD）、场景障碍物、碰撞豁免对、姿态碰撞检查。
    /// 共享依赖通过根 MainWindowViewModel 访问；pendingDecomposition 由根 VM 持有，
    /// 互斥状态（CanInteract/CanPlan/CanGenerateHulls 等）由根 VM 统一计算。
    /// </summary>
    public sealed partial class CollisionViewModel : ObservableObject
    {
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

        private readonly MainWindowViewModel root;
        private bool updatingUi;
        private (uint Index, List<CollisionDemoModel.Hull> Hulls)? candidate;

        // ---- 碰撞模型状态 ----

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

        // ---- 场景障碍物 ----

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

        // ---- 碰撞豁免对 ----

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

        // ---- 互斥状态转发（根 VM 计算） ----

        public bool CanInteract => root.CanInteract;
        public bool CanPlan => root.CanPlan;
        public bool CanGenerateHulls => root.CanGenerateHulls;
        public bool CanEditCollisionLink => root.CanEditCollisionLink;
        public bool CanEditObstacle => root.CanEditObstacle;
        public bool CanEditPair => root.CanEditPair;

        // ---- 命令 ----

        public ICommand IgnorePairCommand { get; }
        public ICommand CheckPairCommand { get; }

        public CollisionViewModel(MainWindowViewModel root)
        {
            this.root = root;
            root.PropertyChanged += OnRootPropertyChanged;
            IgnorePairCommand = new RelayCommand(() => SetPairIgnored(true));
            CheckPairCommand = new RelayCommand(() => SetPairIgnored(false));
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
                case nameof(MainWindowViewModel.CanGenerateHulls):
                    OnPropertyChanged(nameof(CanGenerateHulls));
                    break;
                case nameof(MainWindowViewModel.CanEditCollisionLink):
                    OnPropertyChanged(nameof(CanEditCollisionLink));
                    break;
                case nameof(MainWindowViewModel.CanEditObstacle):
                    OnPropertyChanged(nameof(CanEditObstacle));
                    break;
                case nameof(MainWindowViewModel.CanEditPair):
                    OnPropertyChanged(nameof(CanEditPair));
                    break;
            }
        }

        // ============ 状态刷新 ============

        public void RefreshAllUi() => RefreshCollisionUi();

        public void RefreshCollisionUi()
        {
            var model = root.Workspace.Collision;
            var body = root.Workspace.Body;
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
                root.Workspace.Collision?.ClearCandidate();
                root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
            }
            CollisionStatusText = "已选择 " + value.Name;
        }

        private void RefreshObstacleUi()
        {
            var model = root.Workspace.Collision;
            updatingUi = true;
            try
            {
                ObstacleItems.Clear();
                if (model == null)
                    return;
                foreach (var item in model.Obstacles)
                    ObstacleItems.Add(new ObstacleItem(item, item.Name));
                if (ObstacleItems.Count > 0)
                {
                    SelectedObstacleItem = ObstacleItems[0];
                    LoadObstacleFields(ObstacleItems[0].Value);
                }
                else
                {
                    ResetObstacleFieldsCore();
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
            ObstacleX = MainWindowViewModel.Format(item.X);
            ObstacleY = MainWindowViewModel.Format(item.Y);
            ObstacleZ = MainWindowViewModel.Format(item.Z);
            ObstacleWidth = MainWindowViewModel.Format(item.Width);
            ObstacleDepth = MainWindowViewModel.Format(item.Depth);
            ObstacleHeight = MainWindowViewModel.Format(item.Height);
        }

        private void RefreshPairUi()
        {
            var model = root.Workspace.Collision;
            var body = root.Workspace.Body;
            updatingUi = true;
            try
            {
                PairOwner1Items.Clear();
                PairOwner2Items.Clear();
                IgnoredPairItems.Clear();
                if (model == null || body == null)
                {
                    IgnoredPairCountText = "0 对已忽略";
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

        // 播放帧回调：更新凸分解进度
        public void UpdateDecomposeProgress()
        {
            var pending = root.PendingDecomposition;
            if (pending != null)
            {
                var progress = pending.GetProgress();
                CollisionStatusText = "正在生成凸包 " +
                    progress.ToString("0", CultureInfo.InvariantCulture) + "%";
                DecomposeProgress = Math.Clamp(progress * 100, 0, 100);
            }
            else if (DecomposeProgress != 0)
            {
                DecomposeProgress = 0;
            }
        }

        // ============ 命令：凸分解 ============

        [RelayCommand]
        private async Task GenerateHullsAsync()
        {
            if (root.PendingDecomposition != null)
            {
                root.PendingDecomposition.Cancel();
                CollisionStatusText = "正在取消凸分解…";
                return;
            }
            if (!root.EnsureNoActiveOperation())
                return;
            await root.RunTrackedAsync(GenerateHullsCore);
        }

        private async Task GenerateHullsCore()
        {
            var model = root.Workspace.Collision;
            var body = root.Workspace.Body;
            if (model == null || body == null || root.Workspace.LoadLinkMesh == null ||
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
                using var mesh = root.Workspace.LoadLinkMesh(model.LinkName(linkIndex));
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
                root.PendingDecomposition = session;
                GenerateHullsContent = "取消生成";
                CollisionStatusText = "正在生成凸包…";
                using var result = await Task.Run(session.Compute);
                if (root.IsClosing)
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
                root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
            }
            catch (Exception error)
            {
                if (!root.IsClosing)
                    CollisionStatusText = "凸分解失败：" + error.Message;
            }
            finally
            {
                root.PendingDecomposition = null;
                GenerateHullsContent = "生成 / 重新生成";
                root.RefreshInteractionState();
            }
        }

        [RelayCommand]
        private void ConfirmHulls()
        {
            if (!root.EnsureRobotIdle())
                return;
            if (root.Workspace.Collision == null || candidate is not { } next)
            {
                CollisionStatusText = "请先生成候选凸包。";
                return;
            }
            root.Workspace.Collision.SetLinkHulls(next.Index, next.Hulls);
            candidate = null;
            root.Workspace.Collision.ClearCandidate();
            RefreshCollisionUi();
            root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已确认 " + next.Hulls.Count + " 个凸包。";
        }

        [RelayCommand]
        private void DeleteLinkHulls()
        {
            if (!root.EnsureRobotIdle())
                return;
            var model = root.Workspace.Collision;
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
            root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已删除 Link 凸包：" + model.LinkName(linkIndex);
        }

        // ============ 命令：场景障碍物 ============

        [RelayCommand]
        private void AddObstacle()
        {
            if (!root.EnsureRobotIdle())
                return;
            var model = root.Workspace.Collision;
            if (model == null)
            {
                CollisionStatusText = "请先加载机器人。";
                return;
            }
            model.AddObstacle();
            RefreshCollisionUi();
            root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "已添加场景障碍物。";
        }

        [RelayCommand]
        private void UpdateObstacle()
        {
            if (!root.EnsureRobotIdle())
                return;
            var model = root.Workspace.Collision;
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
            root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText = "障碍物已更新：" + item.Name;
        }

        [RelayCommand]
        private void DeleteObstacle()
        {
            if (!root.EnsureRobotIdle())
                return;
            var model = root.Workspace.Collision;
            if (model == null || SelectedObstacleItem?.Value is not { } item)
                return;
            model.RemoveObstacle(item);
            RefreshCollisionUi();
            root.RenderHost.RequestDraw(EnumUpdateFlags.Scene);
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

        // ============ 命令：碰撞豁免对 ============

        private void SetPairIgnored(bool value)
        {
            var model = root.Workspace.Collision;
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

        // ============ 命令：碰撞检查 ============

        [RelayCommand]
        private async Task RunCollisionCheckAsync()
        {
            if (!root.EnsureNoActiveOperation())
                return;
            await root.RunTrackedAsync(CollisionCheckCore);
        }

        private async Task CollisionCheckCore()
        {
            var body = root.Workspace.Body;
            var model = root.Workspace.Collision;
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
            root.ResetPlayer();
            try
            {
                using var session = new RobotSimulationSession(body, root.Backends);
                root.PendingPlan = session;
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
                if (root.IsClosing)
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
                if (!root.IsClosing)
                    CollisionStatusText = "碰撞检查失败：" + error.Message;
            }
            finally
            {
                root.PendingPlan = null;
                root.RefreshInteractionState();
            }
        }

        private static void AddCurrentJointGoals(RobotBody body, RobotJointPositionList goals,
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

        private bool TryReadObstacle(out double x, out double y, out double z,
            out double w, out double d, out double h)
        {
            x = y = z = w = d = h = 0;
            return MainWindowViewModel.TryParseNumber(ObstacleX, out x) &&
                MainWindowViewModel.TryParseNumber(ObstacleY, out y) &&
                MainWindowViewModel.TryParseNumber(ObstacleZ, out z) &&
                MainWindowViewModel.TryParseNumber(ObstacleWidth, out w) &&
                MainWindowViewModel.TryParseNumber(ObstacleDepth, out d) &&
                MainWindowViewModel.TryParseNumber(ObstacleHeight, out h);
        }
    }
}
