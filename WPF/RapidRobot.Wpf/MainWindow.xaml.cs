using AnyCAD.Foundation;
using AnyCAD.Simulate;
using AnyCAD.WPF;
using Microsoft.Win32;
using RapidRobot;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace RapidRobot.Wpf
{
    public partial class MainWindow : Window
    {
        private sealed record TeachPoint(DateTimeOffset CreatedAt, double[] Position,
            double[] Orientation, double[] Joints);

        private readonly RenderControl renderView;
        private readonly RobotWorkspaceSession workspace = new();
        private readonly DispatcherTimer playbackTimer;
        private readonly Stopwatch playbackClock = new();
        private RobotTrajectoryPlayer player = new();
        private readonly BackendSelection backends;
        private readonly List<TeachPoint> teachPoints = new();
        private RobotSimulationSession? pendingPlan;
        private ConvexDecompositionSession? pendingDecomposition;
        private Task? activeOperation;
        private (uint Index, List<CollisionDemoModel.Hull> Hulls)? candidate;
        private bool viewerReady;
        private bool loadingModel;
        private bool updatingPlayback;
        private bool updatingUi;
        private bool closing;
        private bool shutdownStarted;
        private bool shutdownComplete;
        private string modelLabel = "未加载";

        public MainWindow()
        {
            InitializeComponent();
            workspace.Changed += OnWorkspaceChanged;
            renderView = new RenderControl();
            renderView.ViewerReady += RenderViewOnViewerReady;
            ViewportHost.Content = renderView;
            backends = SimulationBackends.GetDefaults();
            playbackTimer = new DispatcherTimer(DispatcherPriority.Render) {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            playbackTimer.Tick += PlaybackTimerOnTick;
            BindControls();
            playbackTimer.Start();
        }

        private void BindControls()
        {
            ImportUrdfButton.Click += (_, _) => ImportUrdf();
            var manualMenu = new ContextMenu { PlacementTarget = ManualAssemblyButton };
            var kr500Item = new MenuItem { Header = "Kr500（STL）" };
            kr500Item.Click += (_, _) => LoadManualKr500();
            var threeAItem = new MenuItem { Header = "3A（BREP）" };
            threeAItem.Click += (_, _) => LoadManual3A();
            manualMenu.Items.Add(kr500Item);
            manualMenu.Items.Add(threeAItem);
            ManualAssemblyButton.ContextMenu = manualMenu;
            ManualAssemblyButton.Click += (_, _) => manualMenu.IsOpen = true;
            EmergencyStopButton.Click += async (_, _) => await EmergencyStopAsync();
            LinkVisibilityToggle.Checked += (_, _) => SetLinkVisibility();
            LinkVisibilityToggle.Unchecked += (_, _) => SetLinkVisibility();
            TrajectoryVisibilityToggle.Checked += (_, _) => SetTrajectoryVisibility();
            TrajectoryVisibilityToggle.Unchecked += (_, _) => SetTrajectoryVisibility();
            GridVisibilityToggle.Checked += (_, _) => SetGridVisibility();
            GridVisibilityToggle.Unchecked += (_, _) => SetGridVisibility();
            FitViewButton.Click += (_, _) => FitView();
            JogStepSlider.ValueChanged += (_, _) => RefreshJogStep();
            ArmComboBox.SelectionChanged += (_, _) => SelectArmFromUi();
            ApplyPoseButton.Click += (_, _) => ApplyPose();
            RecordPointButton.Click += (_, _) => RecordPoint();
            HomeButton.Click += (_, _) => ResetRobot();
            SaveTeachPointButton.Click += (_, _) => SaveTeachPoints();
            GenerateHullsButton.Click += async (_, _) => await GenerateHullsFromUi();
            CollisionLinkComboBox.SelectionChanged += (_, _) => SelectCollisionLinkFromUi();
            ConfirmHullsButton.Click += (_, _) => ConfirmHulls();
            AddObstacleButton.Click += (_, _) => AddObstacle();
            CollisionCheckButton.Click += async (_, _) => await RunCollisionCheck();
            UseCurrentPoseButton.Click += (_, _) => CopyCurrentPoseToTarget();
            PlanPathButton.Click += async (_, _) => await PlanPathFromUi();
            PlaybackRestartButton.Click += (_, _) => RestartPlayback();
            PlaybackPlayButton.Click += (_, _) => TogglePlayback();
            PlaybackStopButton.Click += (_, _) => StopPlaybackOrCancel();
            PlaybackSlider.ValueChanged += (_, _) => SeekPlayback();
            PlannerComboBox.SelectionChanged += (_, _) => ApplyBackendSelection();
            CollisionBackendComboBox.SelectionChanged += (_, _) => ApplyBackendSelection();

            for (var i = 0; i < 6; ++i)
            {
                var index = i;
                JogNegative(i).Click += (_, _) => JogJoint(index, -1);
                JogPositive(i).Click += (_, _) => JogJoint(index, 1);
            }
        }

        private Button JogNegative(int index) => index switch {
            0 => JogNegative0,
            1 => JogNegative1,
            2 => JogNegative2,
            3 => JogNegative3,
            4 => JogNegative4,
            _ => JogNegative5
        };

        private Button JogPositive(int index) => index switch {
            0 => JogPositive0,
            1 => JogPositive1,
            2 => JogPositive2,
            3 => JogPositive3,
            4 => JogPositive4,
            _ => JogPositive5
        };

        private TextBlock JointValue(int index) => index switch {
            0 => JointValue0,
            1 => JointValue1,
            2 => JointValue2,
            3 => JointValue3,
            4 => JointValue4,
            _ => JointValue5
        };

        private void RenderViewOnViewerReady()
        {
            if (viewerReady)
                return;
            viewerReady = true;
            renderView.SetBackgroundColor(0.035f, 0.055f, 0.09f, 1.0f);
            renderView.ShowCoordinateGrid(GridVisibilityToggle.IsChecked == true);
            renderView.Camera.SetProjectionType(EnumProjectionType.Orthographic);
            renderView.SceneManager.SetViewScale(1);
            renderView.Viewer.SetStandardView(EnumStandardView.DefaultView, false);
            renderView.RequestDraw(EnumUpdateFlags.Scene | EnumUpdateFlags.Camera);
            Dispatcher.InvokeAsync(() =>
            {
                if (workspace.Body == null)
                    LoadManualKr500();
            });
        }

        private void ImportUrdf()
        {
            if (!EnsureViewerReady() || !EnsureNoActiveOperation())
                return;
            var dialog = new OpenFileDialog {
                Filter = "URDF robot model (*.urdf)|*.urdf",
                Title = "Open robot URDF",
                InitialDirectory = GetModelDirectory(null)
            };
            if (dialog.ShowDialog(this) != true)
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

        private void RefreshInteractionUi()
        {
            var canInteract = workspace.Body != null && activeOperation == null &&
                !loadingModel && player.GetState() != EnumRobotPlaybackState.Playing;
            ArmComboBox.IsEnabled = canInteract;
            ApplyPoseButton.IsEnabled = canInteract;
            HomeButton.IsEnabled = canInteract;
            RecordPointButton.IsEnabled = canInteract;
            ConfirmHullsButton.IsEnabled = canInteract;
            AddObstacleButton.IsEnabled = canInteract;
            if (pendingPlan == null)
            {
                CollisionCheckButton.IsEnabled = canInteract;
                PlanPathButton.IsEnabled = canInteract;
            }
            GenerateHullsButton.IsEnabled = pendingDecomposition != null || canInteract;
            CollisionLinkComboBox.IsEnabled = pendingDecomposition == null && canInteract;
            RefreshJointUi();
            UpdatePlaybackUi();
        }

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
            ResetPlayer();
            ResetPathMetrics();
            SetStatus("正在安装机器人模型…");
            var installed = false;
            try
            {
                workspace.Load(loaded, renderView.ClearAll);
                installed = true;
                var controller = workspace.Controller!;
                var body = workspace.Body!;
                renderView.ShowSceneNode(body);
                renderView.ShowSceneNode(controller.Trajectory);
                renderView.ShowSceneNode(workspace.CollisionPreview!);
                controller.ShowLinks(LinkVisibilityToggle.IsChecked != false);
                controller.ShowTracking(TrajectoryVisibilityToggle.IsChecked != false);
                renderView.Viewer.SetStandardView(EnumStandardView.DefaultView, false);
                renderView.ZoomAll();
                modelLabel = label;
                UpdateAllUi();
                UpdateDefaultTarget();
                ViewerEmptyOverlay.Visibility = Visibility.Collapsed;
                SetStatus(label + " 已加载");
            }
            catch
            {
                if (installed)
                {
                    try
                    {
                        renderView.ClearAll();
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

        private void OnWorkspaceChanged()
        {
            if (closing)
                return;
            try
            {
                UpdateAllUi();
                if (viewerReady)
                    renderView.RequestDraw(EnumUpdateFlags.Scene);
            }
            catch (Exception error)
            {
                SetStatus("界面刷新失败：" + error.Message, true);
            }
        }

        private void UpdateAllUi()
        {
            var controller = workspace.Controller;
            var body = workspace.Body;
            SceneResourceName.Text = (body == null ? "未加载" : modelLabel + "_ROBOT");
            SceneResourceStatus.Text = body == null ? "未加载" : "已加载";
            ViewerEmptyOverlay.Visibility = body == null ? Visibility.Visible : Visibility.Collapsed;
            RobotConnectionIndicator.Background = body == null
                ? new SolidColorBrush(Color.FromRgb(148, 163, 184))
                : new SolidColorBrush(Color.FromRgb(34, 197, 94));
            RobotDofText.Text = controller == null
                ? "未加载"
                : modelLabel + " · " + controller.ArmCount + " ARM";
            var canInteract = controller != null && activeOperation == null &&
                player.GetState() != EnumRobotPlaybackState.Playing;
            ArmComboBox.IsEnabled = canInteract;
            ApplyPoseButton.IsEnabled = canInteract;
            HomeButton.IsEnabled = canInteract;
            RecordPointButton.IsEnabled = canInteract;
            SaveTeachPointButton.IsEnabled = controller != null;
            ConfirmHullsButton.IsEnabled = canInteract;
            AddObstacleButton.IsEnabled = canInteract;
            CollisionCheckButton.IsEnabled = canInteract;
            PlanPathButton.IsEnabled = canInteract;
            RefreshArmUi();
            RefreshJointUi();
            RefreshPoseUi();
            RefreshCollisionUi();
            UpdatePlaybackUi();
            GlobalStatusText.Text = body == null ? "等待加载机器人模型" : "安全状态：可操作";
        }

        private void RefreshArmUi()
        {
            var controller = workspace.Controller;
            updatingUi = true;
            try
            {
                ArmComboBox.Items.Clear();
                if (controller != null)
                {
                    for (uint i = 0; i < controller.ArmCount; ++i)
                    {
                        var name = controller.GetArmName(i);
                        ArmComboBox.Items.Add(new ComboBoxItem {
                            Content = "Arm " + (i + 1) + " · " + (string.IsNullOrWhiteSpace(name) ? "Robot" : name)
                        });
                    }
                    var selected = Math.Min((int)controller.SelectedArmIndex, ArmComboBox.Items.Count - 1);
                    ArmComboBox.SelectedIndex = selected;
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
            for (var i = 0; i < 6; ++i)
            {
                var enabled = false;
                var value = double.NaN;
                var type = EnumRobotJointType.Fixed;
                if (controller != null && i < controller.GetJointCount(controller.SelectedArmIndex))
                {
                    type = controller.GetJointType(controller.SelectedArmIndex, (uint)i);
                    enabled = type != EnumRobotJointType.Fixed;
                    value = controller.GetJointPosition(controller.SelectedArmIndex, (uint)i);
                }
                JogNegative(i).IsEnabled = enabled && canInteract;
                JogPositive(i).IsEnabled = enabled && canInteract;
                if (!double.IsFinite(value) || type == EnumRobotJointType.Fixed)
                {
                    JointValue(i).Text = "—";
                    continue;
                }
                var display = value;
                var unit = "mm";
                if (type == EnumRobotJointType.Revolute || type == EnumRobotJointType.Continuous)
                {
                    display *= 180.0 / Math.PI;
                    unit = "°";
                }
                JointValue(i).Text = display.ToString("0.000", CultureInfo.InvariantCulture) + " " + unit;
            }
            RefreshJogStep();
        }

        private void RefreshJogStep()
        {
            var type = GetStepJointType();
            if (type == null)
            {
                JogStepText.Text = "—";
                return;
            }
            var step = GetJogStep(type.Value);
            var unit = type == EnumRobotJointType.Prismatic ? "mm" : "°";
            JogStepText.Text = step.ToString("0.000", CultureInfo.InvariantCulture) + " " + unit;
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
                ? Math.Max(0.1, JogStepSlider.Value / 7.0)
                : Math.Max(0.1, JogStepSlider.Value / 35.0);
        }

        private void SelectArmFromUi()
        {
            if (updatingUi || workspace.Controller == null || ArmComboBox.SelectedIndex < 0)
                return;
            if (!EnsureRobotIdle())
                return;
            var armIndex = (uint)ArmComboBox.SelectedIndex;
            if (!workspace.Controller.SelectArm(armIndex))
            {
                RefreshArmUi();
                SetStatus("无法选择该机械臂。", true);
                return;
            }
            UpdateDefaultTarget();
            SetStatus("已选择 " + workspace.Controller.GetArmName(armIndex));
        }

        private void JogJoint(int index, double direction)
        {
            if (!EnsureRobotIdle())
                return;
            var controller = workspace.Controller;
            if (controller == null || index < 0 || index >= controller.GetJointCount(controller.SelectedArmIndex))
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

        private void ApplyPose()
        {
            if (!EnsureRobotIdle())
                return;
            var controller = workspace.Controller;
            if (controller == null)
                return;
            if (!TryReadPose(PoseXTextBox, PoseYTextBox, PoseZTextBox,
                    PoseRxTextBox, PoseRyTextBox, PoseRzTextBox,
                    out var x, out var y, out var z, out var rx, out var ry, out var rz))
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

        private void ResetRobot()
        {
            if (!EnsureRobotIdle())
                return;
            workspace.Controller?.Reset();
            SetStatus("机器人已回到 Home");
        }

        private async Task EmergencyStopAsync()
        {
            try
            {
                pendingPlan?.Cancel();
                pendingDecomposition?.Cancel();
                player.Stop();
                playbackClock.Reset();
                UpdatePlaybackUi();
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

        private void SetLinkVisibility()
        {
            workspace.Controller?.ShowLinks(LinkVisibilityToggle.IsChecked == true);
        }

        private void SetTrajectoryVisibility()
        {
            workspace.Controller?.ShowTracking(TrajectoryVisibilityToggle.IsChecked == true);
        }

        private void SetGridVisibility()
        {
            if (viewerReady)
                renderView.ShowCoordinateGrid(GridVisibilityToggle.IsChecked == true);
        }

        private void FitView()
        {
            if (viewerReady && workspace.Body != null)
                renderView.ZoomAll();
        }

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

        private void SaveTeachPoints()
        {
            var dialog = new SaveFileDialog {
                Filter = "AnyRobot teach points (*.json)|*.json",
                FileName = "teach-points.json"
            };
            if (dialog.ShowDialog(this) != true)
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

        private void UpdateDefaultTarget()
        {
            if (!TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PlanTargetXTextBox.Text = Format(x - 450.0);
            PlanTargetYTextBox.Text = Format(y);
            PlanTargetZTextBox.Text = Format(z);
            PlanTargetRxTextBox.Text = Format(rx * 180.0 / Math.PI) + "°";
            PlanTargetRyTextBox.Text = Format(ry * 180.0 / Math.PI) + "°";
            PlanTargetRzTextBox.Text = Format(rz * 180.0 / Math.PI) + "°";
        }

        private void CopyCurrentPoseToTarget()
        {
            if (!TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PlanTargetXTextBox.Text = Format(x);
            PlanTargetYTextBox.Text = Format(y);
            PlanTargetZTextBox.Text = Format(z);
            PlanTargetRxTextBox.Text = Format(rx * 180.0 / Math.PI) + "°";
            PlanTargetRyTextBox.Text = Format(ry * 180.0 / Math.PI) + "°";
            PlanTargetRzTextBox.Text = Format(rz * 180.0 / Math.PI) + "°";
            SetStatus("目标位姿已复制当前末端姿态");
        }

        private void RefreshPoseUi()
        {
            var controller = workspace.Controller;
            if (controller == null || !TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PoseXTextBox.Text = Format(x);
            PoseYTextBox.Text = Format(y);
            PoseZTextBox.Text = Format(z);
            PoseRxTextBox.Text = Format(rx * 180.0 / Math.PI) + "°";
            PoseRyTextBox.Text = Format(ry * 180.0 / Math.PI) + "°";
            PoseRzTextBox.Text = Format(rz * 180.0 / Math.PI) + "°";
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

        private static bool TryReadPose(TextBox xBox, TextBox yBox, TextBox zBox,
            TextBox rxBox, TextBox ryBox, TextBox rzBox,
            out double x, out double y, out double z,
            out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            return TryParseNumber(xBox.Text, out x) && TryParseNumber(yBox.Text, out y) &&
                TryParseNumber(zBox.Text, out z) && TryParseNumber(rxBox.Text, out rx) &&
                TryParseNumber(ryBox.Text, out ry) && TryParseNumber(rzBox.Text, out rz);
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

        private void RefreshCollisionUi()
        {
            var model = workspace.Collision;
            var body = workspace.Body;
            var previousIndex = CollisionLinkComboBox.SelectedItem is ComboBoxItem previous &&
                previous.Tag is int previousValue
                ? previousValue
                : 0;
            updatingUi = true;
            try
            {
                CollisionLinkListHost.Children.Clear();
                CollisionLinkComboBox.Items.Clear();
                if (model == null || body == null)
                {
                    CollisionCoverageText.Text = "0%";
                    CollisionCoverageDetail.Text = "尚未加载机器人";
                    CollisionCoverageProgress.Value = 0;
                    CollisionStatusText.Text = "等待加载机器人";
                    CollisionLinkComboBox.IsEnabled = false;
                    return;
                }

                var total = checked((int)body.GetLinkCount());
                var confirmed = model.LinkHulls.Count;
                var coverage = total == 0 ? 0 : confirmed * 100.0 / total;
                CollisionCoverageText.Text = coverage.ToString("0", CultureInfo.InvariantCulture) + "%";
                CollisionCoverageDetail.Text = confirmed + " / " + total + " 个 Link 已确认";
                CollisionCoverageProgress.Value = coverage;
                for (uint i = 0; i < body.GetLinkCount(); ++i)
                {
                    var count = model.LinkHulls.TryGetValue(i, out var hulls) ? hulls.Count : 0;
                    var name = model.LinkName(i);
                    CollisionLinkComboBox.Items.Add(new ComboBoxItem {
                        Content = name + (count == 0 ? " · 待生成" : " · " + count + " 凸包"),
                        Tag = (int)i
                    });
                    CollisionLinkListHost.Children.Add(CreateCollisionRow(name, count));
                }
                if (CollisionLinkComboBox.Items.Count > 0)
                    CollisionLinkComboBox.SelectedIndex = Math.Clamp(previousIndex, 0,
                        CollisionLinkComboBox.Items.Count - 1);
            }
            finally
            {
                updatingUi = false;
            }
            CollisionLinkComboBox.IsEnabled = pendingDecomposition == null && activeOperation == null &&
                player.GetState() != EnumRobotPlaybackState.Playing;
            CollisionStatusText.Text = candidate == null
                ? "已加载 " + model.Obstacles.Count + " 个场景实体"
                : "候选凸包待确认";
        }

        private void SelectCollisionLinkFromUi()
        {
            if (updatingUi || CollisionLinkComboBox.SelectedItem is not ComboBoxItem)
                return;
            if (candidate != null)
            {
                candidate = null;
                workspace.Collision?.ClearCandidate();
                renderView.RequestDraw(EnumUpdateFlags.Scene);
            }
            CollisionStatusText.Text = "已选择 " +
                ((ComboBoxItem)CollisionLinkComboBox.SelectedItem).Content;
        }

        private static Border CreateCollisionRow(string name, int hullCount)
        {
            var grid = new Grid { Height = 36 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            var nameText = new TextBlock {
                Text = name,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 0);
            grid.Children.Add(nameText);
            var countText = new TextBlock {
                Text = hullCount == 0 ? "—" : hullCount.ToString(CultureInfo.InvariantCulture),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.SlateGray
            };
            Grid.SetColumn(countText, 1);
            grid.Children.Add(countText);
            var status = new TextBlock {
                Text = hullCount == 0 ? "待生成" : "已确认",
                Foreground = hullCount == 0 ? Brushes.DarkOrange : Brushes.ForestGreen,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 2);
            grid.Children.Add(status);
            return new Border {
                Child = grid,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 2, 0, 2)
            };
        }

        private async Task GenerateHullsFromUi()
        {
            if (pendingDecomposition != null)
            {
                pendingDecomposition.Cancel();
                CollisionStatusText.Text = "正在取消凸分解…";
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
                CollisionLinkComboBox.SelectedItem is not ComboBoxItem selected ||
                selected.Tag is not int selectedIndex)
            {
                CollisionStatusText.Text = "请先加载机器人并选择 Link";
                return;
            }
            var linkIndex = checked((uint)selectedIndex);
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
                GenerateHullsButton.Content = "取消生成";
                CollisionStatusText.Text = "正在生成凸包…";
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
                CollisionStatusText.Text = "候选预览已生成，共 " + hulls.Count + " 个凸包，请确认。";
                RefreshCollisionUi();
                renderView.RequestDraw(EnumUpdateFlags.Scene);
            }
            catch (Exception error)
            {
                if (!closing)
                    CollisionStatusText.Text = "凸分解失败：" + error.Message;
            }
            finally
            {
                pendingDecomposition = null;
                GenerateHullsButton.Content = "生成 / 重新生成";
                RefreshInteractionUi();
            }
        }

        private void ConfirmHulls()
        {
            if (!EnsureRobotIdle())
                return;
            if (workspace.Collision == null || candidate is not { } next)
            {
                CollisionStatusText.Text = "请先生成候选凸包。";
                return;
            }
            workspace.Collision.SetLinkHulls(next.Index, next.Hulls);
            candidate = null;
            workspace.Collision.ClearCandidate();
            RefreshCollisionUi();
            renderView.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText.Text = "已确认 " + next.Hulls.Count + " 个凸包。";
        }

        private void AddObstacle()
        {
            if (!EnsureRobotIdle())
                return;
            var model = workspace.Collision;
            if (model == null)
            {
                CollisionStatusText.Text = "请先加载机器人。";
                return;
            }
            model.AddObstacle();
            RefreshCollisionUi();
            renderView.RequestDraw(EnumUpdateFlags.Scene);
            CollisionStatusText.Text = "已添加场景障碍物。";
        }

        private async Task RunCollisionCheck()
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
                CollisionStatusText.Text = "请先加载机器人。";
                return;
            }
            if (model.LinkHulls.Count == 0)
            {
                CollisionStatusText.Text = "请先生成并确认至少一个 Link 凸包。";
                return;
            }
            ResetPlayer();
            CollisionCheckButton.IsEnabled = false;
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
                CollisionStatusText.Text = "正在检查当前姿态…";
                using var result = await Task.Run(() => session.Plan(request));
                if (closing)
                    return;
                var modeled = model.LinkHulls.Count;
                var total = body.GetLinkCount();
                if (result.Succeeded())
                    CollisionStatusText.Text = modeled == total
                        ? "全部 Link 已建模，当前姿态未检测到碰撞。"
                        : "已建模的 " + modeled + " / " + total +
                          " 个 Link 未检测到碰撞；未建模 Link 未检查。";
                else
                {
                    using var planning = result.Planning;
                    CollisionStatusText.Text = "当前姿态发生碰撞：" + planning.Status + " " + planning.Diagnostic;
                }
            }
            catch (Exception error)
            {
                if (!closing)
                    CollisionStatusText.Text = "碰撞检查失败：" + error.Message;
            }
            finally
            {
                pendingPlan = null;
                CollisionCheckButton.IsEnabled = true;
            }
        }

        private async Task PlanPathFromUi()
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
                PlanStatusText.Text = "请先加载机器人。";
                return;
            }
            if (!TryReadPose(PlanTargetXTextBox, PlanTargetYTextBox, PlanTargetZTextBox,
                    PlanTargetRxTextBox, PlanTargetRyTextBox, PlanTargetRzTextBox,
                    out var x, out var y, out var z, out var rx, out var ry, out var rz))
            {
                PlanStatusText.Text = "目标位姿包含无效数字。";
                return;
            }
            if (!TryParseNumber(CollisionResolutionTextBox.Text, out var collisionResolution) ||
                collisionResolution <= 0)
            {
                PlanStatusText.Text = "碰撞采样间隔必须是大于 0 的有限数值。";
                return;
            }
            if (!TryParseNumber(PlanTimeoutTextBox.Text, out var timeoutSeconds) ||
                timeoutSeconds <= 0)
            {
                PlanStatusText.Text = "规划超时必须是大于 0 的有限数值。";
                return;
            }
            if (model.LinkHulls.Count == 0)
            {
                PlanStatusText.Text = "请先生成并确认至少一个 Link 凸包。";
                return;
            }
            ResetPlayer();
            PlanPathButton.IsEnabled = false;
            PlanStatusText.Text = "正在规划无碰撞路径…";
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
                    PlanStatusText.Text = planning.Status + ": " + planning.Diagnostic;
                    return;
                }
                using (var planning = result.Planning)
                using (var path = planning.Path)
                {
                    PathSamplesText.Text = path.Count.ToString(CultureInfo.InvariantCulture);
                    PathLengthText.Text = CalculatePathLength(path).ToString("0.000", CultureInfo.InvariantCulture);
                }
                string backendText;
                using (var selectedBackends = result.Backends)
                    backendText = selectedBackends.Planner + " / " + selectedBackends.Collision;
                if (!player.Load(body, result))
                    throw new InvalidOperationException(player.GetDiagnostic());
                UpdatePlaybackUi();
                PathDurationText.Text = player.GetDuration().ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlanStatusText.Text = "无碰撞路径已就绪";
                PlanBackendText.Text = backendText;
            }
            catch (Exception error)
            {
                if (!closing)
                    PlanStatusText.Text = "路径规划失败：" + error.Message;
            }
            finally
            {
                pendingPlan = null;
                PlanPathButton.IsEnabled = true;
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

        private void ApplyBackendSelection()
        {
            if (updatingUi)
                return;
            var planner = ComboText(PlannerComboBox, backends.Planner);
            var collision = ComboText(CollisionBackendComboBox, backends.Collision);
            if (string.IsNullOrWhiteSpace(planner) || string.IsNullOrWhiteSpace(collision))
                return;
            try
            {
                backends.Planner = planner;
                backends.Collision = collision;
                using var defaults = new BackendSelection {
                    Ik = backends.Ik,
                    Planner = planner,
                    Collision = collision
                };
                SimulationBackends.SetDefaults(defaults);
                PlanBackendText.Text = planner + " / " + collision;
            }
            catch (Exception error)
            {
                SetStatus("后端设置失败：" + error.Message, true);
            }
        }

        private static string ComboText(ComboBox comboBox, string fallback)
        {
            return comboBox.SelectedItem is ComboBoxItem item
                ? item.Content?.ToString() ?? fallback : fallback;
        }

        private void UpdatePlaybackUi()
        {
            var duration = player.GetDuration();
            var time = player.GetTime();
            var canControl = activeOperation == null && !loadingModel;
            updatingPlayback = true;
            try
            {
                PlaybackSlider.Maximum = Math.Max(1.0, duration);
                PlaybackSlider.Value = Math.Clamp(time, 0, Math.Max(1.0, duration));
                PlaybackSlider.IsEnabled = duration > 0 && canControl;
                PlaybackPlayButton.IsEnabled = duration > 0 && canControl;
                PlaybackRestartButton.IsEnabled = duration > 0 && canControl;
                PlaybackStopButton.IsEnabled = duration > 0 || pendingPlan != null ||
                    pendingDecomposition != null;
                PlaybackTimeText.Text = time.ToString("0.000", CultureInfo.InvariantCulture) + " / " +
                    duration.ToString("0.000", CultureInfo.InvariantCulture) + " s";
                PlaybackPlayButton.Content = player.GetState() == EnumRobotPlaybackState.Playing ? "暂停" : "播放";
            }
            finally
            {
                updatingPlayback = false;
            }
        }

        private void TogglePlayback()
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
            RefreshInteractionUi();
        }

        private void RestartPlayback()
        {
            if (!EnsureRobotIdle())
                return;
            if (player.GetDuration() <= 0)
                return;
            StopPlaybackCore();
            SetStatus("轨迹已回到起点");
        }

        private void StopPlaybackOrCancel()
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
            renderView.RequestDraw(EnumUpdateFlags.Scene);
            UpdatePoseUiFromBody();
            UpdatePlaybackUi();
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
            UpdatePlaybackUi();
        }

        private void ResetPathMetrics()
        {
            PathLengthText.Text = "—";
            PathDurationText.Text = "—";
            PathSamplesText.Text = "—";
        }

        private void SeekPlayback()
        {
            if (updatingPlayback || !EnsureRobotIdle() || player.GetDuration() <= 0)
                return;
            if (player.Seek(PlaybackSlider.Value))
            {
                workspace.Collision?.UpdateLinkTransforms();
                renderView.RequestDraw(EnumUpdateFlags.Scene);
                UpdatePoseUiFromBody();
                UpdatePlaybackUi();
            }
        }

        private void PlaybackTimerOnTick(object? sender, EventArgs e)
        {
            if (pendingDecomposition != null)
            {
                CollisionStatusText.Text = "正在生成凸包 " +
                    pendingDecomposition.GetProgress().ToString("0", CultureInfo.InvariantCulture) + "%";
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
            renderView.RequestDraw(EnumUpdateFlags.Scene);
            UpdatePoseUiFromBody();
            UpdatePlaybackUi();
            if (!advanced)
                RefreshInteractionUi();
        }

        private void UpdatePoseUiFromBody()
        {
            if (workspace.Controller != null)
                RefreshPoseUi();
        }

        private async Task RunTrackedAsync(Func<Task> operation)
        {
            Task? task = null;
            try
            {
                task = operation();
                activeOperation = task;
                RefreshInteractionUi();
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
                    RefreshInteractionUi();
            }
        }

        private void SetStatus(string message, bool error = false)
        {
            GlobalStatusText.Text = message;
            GlobalStatusText.Foreground = error
                ? Brushes.Firebrick
                : new SolidColorBrush(Color.FromRgb(71, 85, 105));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (shutdownComplete || e.Cancel)
                return;
            e.Cancel = true;
            if (shutdownStarted)
                return;
            shutdownStarted = true;
            _ = ShutdownAsync();
        }

        private async Task ShutdownAsync()
        {
            closing = true;
            workspace.Changed -= OnWorkspaceChanged;
            playbackTimer.Stop();
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
            TryCleanup(backends.Dispose);
            TryCleanup(() => renderView.ViewerReady -= RenderViewOnViewerReady);
            TryCleanup(renderView.Dispose);
            shutdownComplete = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
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
    }
}
