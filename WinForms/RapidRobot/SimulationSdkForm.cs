using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RapidRobot
{
    // Exercises only the native Simulate value/model API; no Document or Element.
    internal sealed class SimulationSdkForm : Form
    {
        private readonly RobotBody body;
        private readonly Action redraw;
        private readonly BackendSelection backends = SimulationBackends.GetDefaults();
        private readonly RobotTrajectoryPlayer player = new RobotTrajectoryPlayer();
        private readonly ComboBox jointList = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        private readonly NumericUpDown goal = new NumericUpDown { DecimalPlaces = 4, Minimum = -1000000, Maximum = 1000000, Width = 150 };
        private readonly NumericUpDown velocity = new NumericUpDown { DecimalPlaces = 3, Minimum = 0.001m, Maximum = 1000000, Value = 1 };
        private readonly NumericUpDown acceleration = new NumericUpDown { DecimalPlaces = 3, Minimum = 0.001m, Maximum = 1000000, Value = 2 };
        private readonly NumericUpDown time = new NumericUpDown { DecimalPlaces = 3, Minimum = 0, Maximum = 0 };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(610, 0) };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 16 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private RobotSimulationSession pending;
        private bool updatingTime;

        internal SimulationSdkForm(RobotBody body, Action redraw)
        {
            this.body = body;
            this.redraw = redraw;
            Text = "Simulate SDK — native backends";
            Width = 680;
            Height = 510;
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            Controls.Add(layout);
            AddBackend(layout, "IK", EnumSimulationBackendKind.Ik);
            AddBackend(layout, "Planner", EnumSimulationBackendKind.Planner);
            AddBackend(layout, "Collision", EnumSimulationBackendKind.Collision);
            layout.Controls.Add(new Label { Text = "Joint goal (radians / model length units)", AutoSize = true });
            var jointRow = new FlowLayoutPanel { AutoSize = true };
            jointRow.Controls.Add(jointList);
            jointRow.Controls.Add(goal);
            layout.Controls.Add(jointRow);
            for (uint i = 0; i < body.GetJointCount(); ++i)
            {
                using var index = new RobotJointIndex(i);
                using var joint = body.GetJoint(index);
                if (joint.GetJointType() != EnumRobotJointType.Fixed) jointList.Items.Add(new JointChoice(i, joint.GetName()));
            }
            jointList.SelectedIndexChanged += (_, _) =>
            {
                if (jointList.SelectedItem is not JointChoice choice) return;
                using var index = new RobotJointIndex(choice.Index);
                using var joint = body.GetJoint(index);
                goal.Value = (decimal)body.GetJointPosition(index);
                velocity.Value = (decimal)(joint.GetMaxVelocity() > 0 ? joint.GetMaxVelocity() : 1);
                acceleration.Value = Math.Min(acceleration.Maximum, 2 * velocity.Value);
            };
            if (jointList.Items.Count > 0) jointList.SelectedIndex = 0;
            var limits = new FlowLayoutPanel { AutoSize = true };
            limits.Controls.Add(new Label { Text = "Max velocity", AutoSize = true });
            limits.Controls.Add(velocity);
            limits.Controls.Add(new Label { Text = "Max acceleration", AutoSize = true });
            limits.Controls.Add(acceleration);
            layout.Controls.Add(limits);
            var actions = new FlowLayoutPanel { AutoSize = true };
            Button AddButton(string text, EventHandler action)
            {
                var button = new Button { Text = text, AutoSize = true };
                button.Click += action;
                actions.Controls.Add(button);
                return button;
            }
            AddButton("Plan", async (_, _) => await Plan());
            AddButton("Play", (_, _) => player.Play());
            AddButton("Pause", (_, _) => player.Pause());
            AddButton("Stop / cancel", (_, _) => { pending?.Cancel(); player.Stop(); redraw(); });
            AddButton("Check all-tip IK", (_, _) => CheckIk());
            layout.Controls.Add(actions);
            layout.Controls.Add(time);
            layout.Controls.Add(status);
            time.ValueChanged += (_, _) => { if (!updatingTime && player.Seek((double)time.Value)) redraw(); };
            timer.Tick += (_, _) =>
            {
                double seconds = clock.Elapsed.TotalSeconds;
                clock.Restart();
                if (player.GetState() != EnumRobotPlaybackState.Playing) return;
                if (!player.Tick(seconds)) status.Text = player.GetDiagnostic();
                updatingTime = true;
                time.Value = Math.Clamp((decimal)player.GetTime(), time.Minimum, time.Maximum);
                updatingTime = false;
                redraw();
            };
            timer.Start();
            FormClosed += (_, _) =>
            {
                pending?.Cancel();
                timer.Stop();
                timer.Dispose();
                player.Dispose();
                backends.Dispose();
            };
        }

        private void AddBackend(Control layout, string label, EnumSimulationBackendKind kind)
        {
            var row = new FlowLayoutPanel { AutoSize = true };
            row.Controls.Add(new Label { Text = label, Width = 80 });
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 480 };
            using var descriptors = SimulationBackends.List(kind, true);
            foreach (var descriptor in descriptors)
            {
                using (descriptor)
                {
                    combo.Items.Add(new BackendChoice(descriptor.Name, descriptor.Available,
                        descriptor.Available ? descriptor.Algorithm : descriptor.Diagnostic));
                    string selected = kind == EnumSimulationBackendKind.Ik ? backends.Ik :
                        kind == EnumSimulationBackendKind.Planner ? backends.Planner : backends.Collision;
                    if (descriptor.Name == selected) combo.SelectedIndex = combo.Items.Count - 1;
                }
            }
            int acceptedIndex = combo.SelectedIndex;
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (combo.SelectedItem is not BackendChoice choice) return;
                if (!choice.Available)
                {
                    combo.SelectedIndex = acceptedIndex;
                    status.Text = choice.Detail;
                    return;
                }
                using var defaults = new BackendSelection();
                if (kind == EnumSimulationBackendKind.Ik) defaults.Ik = backends.Ik = choice.Name;
                else if (kind == EnumSimulationBackendKind.Planner) defaults.Planner = backends.Planner = choice.Name;
                else defaults.Collision = backends.Collision = choice.Name;
                SimulationBackends.SetDefaults(defaults);
                acceptedIndex = combo.SelectedIndex;
                status.Text = choice.Detail;
            };
            row.Controls.Add(combo);
            layout.Controls.Add(row);
        }

        private async Task Plan()
        {
            if (pending != null || jointList.SelectedItem is not JointChoice choice) return;
            player.Pause();
            using var session = new RobotSimulationSession(body, backends);
            pending = session;
            try
            {
                using var index = new RobotJointIndex(choice.Index);
                using var goals = new RobotJointPositionList();
                using var position = new RobotJointPosition { JointIndex = index, Position = (double)goal.Value };
                goals.Add(position);
                using var motionLimits = new RobotMotionLimitList();
                using var limit = new RobotMotionLimit { JointIndex = index, MaxVelocity = (double)velocity.Value, MaxAcceleration = (double)acceleration.Value };
                motionLimits.Add(limit);
                using var request = session.CreatePlanningRequest(goals, motionLimits);
                status.Text = "Planning…";
                using var result = await Task.Run(() => session.Plan(request));
                if (IsDisposed) return;
                if (!result.Succeeded()) { using var planning = result.Planning; status.Text = planning.Status + ": " + planning.Diagnostic; return; }
                if (!player.Load(body, result)) { status.Text = player.GetDiagnostic(); return; }
                time.Maximum = (decimal)player.GetDuration();
                using var selected = result.Backends;
                status.Text = selected.Planner + " / " + selected.Collision + ": trajectory ready (" + player.GetDuration().ToString("F3") + " s).";
            }
            catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
            finally { pending = null; }
        }

        private void CheckIk()
        {
            using var targets = new RobotSimulationTargetList();
            for (uint i = 0; i < body.GetArmCount(); ++i)
            {
                using var armIndex = new RobotArmIndex(i);
                using var arm = body.GetArm(armIndex);
                using var frame = arm.GetTipFrameIndex();
                using var transform = body.GetFrameTransform(frame, false);
                using var target = new RobotSimulationTarget { FrameIndex = frame, TargetInBody = transform };
                targets.Add(target);
            }
            using var session = new RobotSimulationSession(body, backends);
            using var result = session.SolveIk(targets);
            status.Text = result.Backend + " / " + result.Algorithm + ": " + result.Status + " " + result.Diagnostic;
        }
        private sealed record JointChoice(uint Index, string Name)
        {
            public override string ToString() => Index + ": " + Name;
        }
        private sealed record BackendChoice(string Name, bool Available, string Detail)
        {
            public override string ToString() => Name + " — " + (Available ? Detail : "unavailable: " + Detail);
        }
    }
}
