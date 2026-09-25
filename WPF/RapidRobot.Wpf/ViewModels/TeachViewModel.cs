using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace RapidRobot.Wpf.ViewModels
{
    /// <summary>
    /// 示教 Tab：机械臂选择、关节点动、末端位姿、示教点记录。
    /// 共享依赖（workspace/renderHost/player 等）通过根 MainWindowViewModel 访问，
    /// 互斥状态（CanInteract 等）由根 VM 统一计算后通过转发属性暴露。
    /// </summary>
    public sealed partial class TeachViewModel : ObservableObject
    {
        private sealed record TeachPoint(DateTimeOffset CreatedAt, double[] Position,
            double[] Orientation, double[] Joints);

        private readonly MainWindowViewModel root;
        private readonly List<TeachPoint> teachPoints = new();
        private bool updatingUi;

        // ---- 机械臂选择 ----

        [ObservableProperty]
        private ObservableCollection<string> armItems = new();

        [ObservableProperty]
        private int selectedArmIndex = -1;

        // ---- 关节点动 ----

        [ObservableProperty]
        private double jogStep = 35;

        [ObservableProperty]
        private string jogStepText = "—";

        [ObservableProperty]
        private string[] jointValues = new string[6];

        [ObservableProperty]
        private bool[] jogEnabled = new bool[6];

        // ---- 末端位姿 ----

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

        // ---- 互斥状态转发（根 VM 计算） ----

        public bool CanInteract => root.CanInteract;
        public bool CanSaveTeachPoints => root.CanSaveTeachPoints;

        // ---- 命令 ----

        public ICommand[] JogNegativeCommands { get; }
        public ICommand[] JogPositiveCommands { get; }

        public TeachViewModel(MainWindowViewModel root)
        {
            this.root = root;
            root.PropertyChanged += OnRootPropertyChanged;
            JogNegativeCommands = new ICommand[6];
            JogPositiveCommands = new ICommand[6];
            for (var i = 0; i < 6; ++i)
            {
                var index = i;
                JogNegativeCommands[i] = new RelayCommand(() => JogJoint(index, -1));
                JogPositiveCommands[i] = new RelayCommand(() => JogJoint(index, 1));
            }
        }

        private void OnRootPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.CanInteract):
                    OnPropertyChanged(nameof(CanInteract));
                    break;
                case nameof(MainWindowViewModel.CanSaveTeachPoints):
                    OnPropertyChanged(nameof(CanSaveTeachPoints));
                    break;
            }
        }

        // ============ 状态刷新 ============

        public void RefreshAllUi()
        {
            RefreshArmUi();
            RefreshJointUi();
            RefreshPoseUi();
        }

        public void RefreshArmUi()
        {
            var controller = root.Workspace.Controller;
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
            root.PlanTab.RefreshSdkJointCombo();
        }

        partial void OnSelectedArmIndexChanged(int value)
        {
            if (updatingUi || root.Workspace.Controller == null || value < 0)
                return;
            if (!root.EnsureRobotIdle())
            {
                RefreshArmUi();
                return;
            }
            var armIndex = (uint)value;
            if (!root.Workspace.Controller.SelectArm(armIndex))
            {
                RefreshArmUi();
                root.SetStatus("无法选择该机械臂。", true);
                return;
            }
            root.PlanTab.UpdateDefaultTarget();
            root.SetStatus("已选择 " + root.Workspace.Controller.GetArmName(armIndex));
        }

        public void RefreshJointUi()
        {
            var controller = root.Workspace.Controller;
            var canInteract = controller != null && root.ActiveOperation == null &&
                !root.IsLoadingModel && root.Player.GetState() != EnumRobotPlaybackState.Playing;
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
            var controller = root.Workspace.Controller;
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

        public void RefreshPoseUi()
        {
            if (!root.TryGetCurrentPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
                return;
            PoseX = MainWindowViewModel.Format(x);
            PoseY = MainWindowViewModel.Format(y);
            PoseZ = MainWindowViewModel.Format(z);
            PoseRx = MainWindowViewModel.Format(rx * 180.0 / Math.PI) + "°";
            PoseRy = MainWindowViewModel.Format(ry * 180.0 / Math.PI) + "°";
            PoseRz = MainWindowViewModel.Format(rz * 180.0 / Math.PI) + "°";
        }

        // ============ 命令：示教 ============

        private void JogJoint(int index, double direction)
        {
            if (!root.EnsureRobotIdle())
                return;
            var controller = root.Workspace.Controller;
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
                root.SetStatus("关节点动失败或已达到限位。", true);
        }

        [RelayCommand]
        private void ApplyPose()
        {
            if (!root.EnsureRobotIdle())
                return;
            var controller = root.Workspace.Controller;
            if (controller == null)
                return;
            if (!TryReadPose(out var x, out var y, out var z,
                    out var rx, out var ry, out var rz))
            {
                root.SetStatus("末端位姿包含无效数字。", true);
                return;
            }
            using var target = RobotAssemblyPose.Create(x, y, z,
                rx * Math.PI / 180.0, ry * Math.PI / 180.0, rz * Math.PI / 180.0);
            if (!controller.TryMoveSelectedArmTip(target))
                root.SetStatus("末端 IK 失败，请检查目标可达性。", true);
            else
                root.SetStatus("末端位姿已应用");
        }

        [RelayCommand]
        private void RecordPoint()
        {
            var controller = root.Workspace.Controller;
            if (controller == null || !root.TryGetCurrentPose(out var x, out var y, out var z,
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
            root.SetStatus("已记录示教点 " + teachPoints.Count);
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
                root.SetStatus("已保存 " + teachPoints.Count + " 个示教点");
            }
            catch (Exception error)
            {
                root.SetStatus("示教点保存失败：" + error.Message, true);
            }
        }

        [RelayCommand]
        private void ResetRobot()
        {
            if (!root.EnsureRobotIdle())
                return;
            root.Workspace.Controller?.Reset();
            root.SetStatus("机器人已回到 Home");
        }

        private bool TryReadPose(out double x, out double y, out double z,
            out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            return MainWindowViewModel.TryParseNumber(PoseX, out x) &&
                MainWindowViewModel.TryParseNumber(PoseY, out y) &&
                MainWindowViewModel.TryParseNumber(PoseZ, out z) &&
                MainWindowViewModel.TryParseNumber(PoseRx, out rx) &&
                MainWindowViewModel.TryParseNumber(PoseRy, out ry) &&
                MainWindowViewModel.TryParseNumber(PoseRz, out rz);
        }
    }
}
