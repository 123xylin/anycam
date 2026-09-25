using AnyCAD.Foundation;
using AnyCAD.Robot.Studio;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RapidRobot
{
    internal sealed class CollisionDemoForm : Form
    {
        private sealed record OwnerChoice(ulong Id, string Name)
        {
            public override string ToString() => Name + " [" + Id + "]";
        }
        private sealed record PairChoice(CollisionDemoModel.Pair Pair, string Label)
        {
            public override string ToString() => Label;
        }

        private readonly RobotBody body;
        private readonly Func<string, CollisionTriangleMesh> loadLinkMesh;
        private readonly Action redraw;
        private readonly CollisionDemoModel model;
        private readonly BackendSelection backends = SimulationBackends.GetDefaults();
        private readonly RobotTrajectoryPlayer player = new RobotTrajectoryPlayer();
        private readonly ComboBox links = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
        private readonly ComboBox firstOwner = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        private readonly ComboBox secondOwner = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
        private readonly ListBox obstacleList = new() { Width = 205, Height = 115 };
        private readonly ListBox pairList = new() { Width = 440, Height = 100 };
        private readonly NumericUpDown[] obstacleValues = new NumericUpDown[6];
        private readonly NumericUpDown[] targetOffsets = new NumericUpDown[3];
        private readonly Label status = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(800, 0) };
        private readonly ProgressBar progress = new() { Width = 300, Height = 20 };
        private readonly NumericUpDown playTime = new() { DecimalPlaces = 3, Minimum = 0, Maximum = 0, Width = 110 };
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private ConvexDecompositionSession pendingDecomposition;
        private RobotSimulationSession pendingPlan;
        private (uint Index, List<CollisionDemoModel.Hull> Hulls)? candidate;
        private bool updatingObstacle, updatingPlayTime;

        internal GroupSceneNode PreviewRoot => model.PreviewRoot;

        internal CollisionDemoForm(RobotBody body,
            Func<string, CollisionTriangleMesh> loadLinkMesh, Action redraw)
        {
            this.body = body;
            this.loadLinkMesh = loadLinkMesh;
            this.redraw = redraw;
            SetKr500DemoPose();
            model = new CollisionDemoModel(body);
            Text = "Collision authoring and Kr500 path planning";
            Width = 850;
            Height = 760;
            var page = new FlowLayoutPanel {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(12)
            };
            Controls.Add(page);
            page.Controls.Add(new Label {
                AutoSize = true, Text = "Link V-HACD • scene entities • collision exclusions • tip planning"
            });

            var linkBox = Section("1. Link convex hulls");
            var linkRow = Row(linkBox);
            linkRow.Controls.Add(links);
            Button(linkRow, "Generate / regenerate", async (_, _) => await Generate());
            Button(linkRow, "Cancel", (_, _) => pendingDecomposition?.Cancel());
            Button(linkRow, "Confirm preview", (_, _) => Confirm());
            Button(linkRow, "Delete", (_, _) => DeleteLink());
            var showPreview = new CheckBox { Text = "Show collision previews", Checked = true, AutoSize = true };
            showPreview.CheckedChanged += (_, _) => {
                model.PreviewRoot.SetVisible(showPreview.Checked);
                redraw();
            };
            linkBox.Controls.Add(showPreview);
            linkBox.Controls.Add(progress);
            linkBox.Controls.Add(new Label {
                AutoSize = true,
                Text = "8 hulls max · 100,000 voxels · 2.5% volume error · depth 8 · 32 vertices/hull"
            });
            page.Controls.Add(linkBox);
            for (uint i = 0; i < body.GetLinkCount(); ++i)
                links.Items.Add(new OwnerChoice((ulong)i + 1, model.LinkName(i)));
            if (links.Items.Count > 0) links.SelectedIndex = 0;
            links.SelectedIndexChanged += (_, _) => {
                candidate = null;
                model.ClearCandidate();
                redraw();
            };

            var sceneBox = Section("2. Scene entities and collision boxes");
            var sceneRow = Row(sceneBox);
            sceneRow.Controls.Add(obstacleList);
            var fields = new TableLayoutPanel { ColumnCount = 2, RowCount = 6, AutoSize = true };
            var names = new[] { "X", "Y", "Z", "Width", "Depth", "Height" };
            for (int i = 0; i < names.Length; ++i)
            {
                fields.Controls.Add(new Label { Text = names[i], AutoSize = true }, 0, i);
                var input = new NumericUpDown {
                    DecimalPlaces = 1, Increment = 50, Minimum = i < 3 ? -10000 : 1,
                    Maximum = 10000, Width = 105
                };
                obstacleValues[i] = input;
                input.ValueChanged += (_, _) => SaveObstacle();
                fields.Controls.Add(input, 1, i);
            }
            sceneRow.Controls.Add(fields);
            var sceneActions = Row(sceneBox);
            Button(sceneActions, "Add box", (_, _) => {
                var item = model.AddObstacle();
                RefreshOwners();
                obstacleList.SelectedItem = item;
                redraw();
            });
            Button(sceneActions, "Delete selected", (_, _) => {
                if (obstacleList.SelectedItem is not CollisionDemoModel.Obstacle item) return;
                model.RemoveObstacle(item);
                RefreshOwners();
                redraw();
            });
            page.Controls.Add(sceneBox);
            obstacleList.SelectedIndexChanged += (_, _) => LoadObstacle();

            var ignoreBox = Section("3. Collision exclusions");
            var pairRow = Row(ignoreBox);
            pairRow.Controls.Add(firstOwner);
            pairRow.Controls.Add(secondOwner);
            Button(pairRow, "Ignore pair", (_, _) => SetPair(true));
            Button(pairRow, "Check pair", (_, _) => SetPair(false));
            ignoreBox.Controls.Add(new Label {
                AutoSize = true, Text = "Adjacent links start ignored. Check pair removes that exemption."
            });
            ignoreBox.Controls.Add(pairList);
            pairList.SelectedIndexChanged += (_, _) => {
                if (pairList.SelectedItem is not PairChoice selected) return;
                SelectOwner(firstOwner, selected.Pair.First);
                SelectOwner(secondOwner, selected.Pair.Second);
            };
            page.Controls.Add(ignoreBox);

            var planBox = Section("4. Kr500 tip collision-free path");
            var targetRow = Row(planBox);
            targetRow.Controls.Add(new Label { Text = "Tip offset from current pose (world X / Y / Z, mm)", AutoSize = true });
            for (int i = 0; i < 3; ++i)
            {
                targetOffsets[i] = new NumericUpDown {
                    DecimalPlaces = 1, Increment = 50, Minimum = -5000,
                    Maximum = 5000, Width = 82, Value = i == 0 ? -450m : 0
                };
                targetRow.Controls.Add(targetOffsets[i]);
            }
            var planRow = Row(planBox);
            Button(planRow, "Plan tip path", async (_, _) => await PlanTip());
            Button(planRow, "Play", (_, _) => player.Play());
            Button(planRow, "Pause", (_, _) => player.Pause());
            Button(planRow, "Stop / cancel", (_, _) => {
                pendingPlan?.Cancel(); player.Stop(); model.UpdateLinkTransforms(); redraw();
            });
            planRow.Controls.Add(new Label { Text = "Time", AutoSize = true });
            planRow.Controls.Add(playTime);
            page.Controls.Add(planBox);
            page.Controls.Add(status);

            playTime.ValueChanged += (_, _) => {
                if (!updatingPlayTime && player.Seek((double)playTime.Value)) {
                    model.UpdateLinkTransforms();
                    redraw();
                }
            };
            timer.Tick += (_, _) => {
                if (pendingDecomposition != null)
                    progress.Value = Math.Clamp((int)(100 * pendingDecomposition.GetProgress()), 0, 100);
                double elapsed = clock.Elapsed.TotalSeconds;
                clock.Restart();
                if (player.GetState() == EnumRobotPlaybackState.Playing)
                {
                    if (!player.Tick(elapsed)) status.Text = player.GetDiagnostic();
                    updatingPlayTime = true;
                    playTime.Value = Math.Clamp((decimal)player.GetTime(), playTime.Minimum, playTime.Maximum);
                    updatingPlayTime = false;
                    model.UpdateLinkTransforms();
                    redraw();
                }
            };
            timer.Start();
            FormClosed += (_, _) => {
                pendingDecomposition?.Cancel();
                pendingPlan?.Cancel();
                player.Stop();
                timer.Stop();
                timer.Dispose();
                player.Dispose();
                backends.Dispose();
                model.PreviewRoot.Clear();
                redraw();
            };
            model.AddObstacle();
            RefreshOwners();
            status.Text = "Generate and confirm Link hulls, then plan the tip around a scene box. The default pose and obstacle are tuned for Kr500.";
            redraw();
        }

        private void SetKr500DemoPose()
        {
            using var axis2 = new RobotJointIndex(1);
            using var axis3 = new RobotJointIndex(2);
            body.SetJointPosition(axis2, -0.45);
            body.SetJointPosition(axis3, 0.6);
            body.UpdateFrames();
        }

        internal void UpdateLinkTransforms() => model.UpdateLinkTransforms();

        private static FlowLayoutPanel Section(string title)
        {
            var panel = new FlowLayoutPanel {
                AutoSize = true, Width = 785, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(7)
            };
            panel.Controls.Add(new Label {
                Text = title, AutoSize = true, Font = new System.Drawing.Font(
                    System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold)
            });
            return panel;
        }
        private static FlowLayoutPanel Row(Control parent)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            parent.Controls.Add(row);
            return row;
        }
        private static void Button(Control parent, string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += action;
            parent.Controls.Add(button);
        }

        private async Task Generate()
        {
            if (pendingDecomposition != null || links.SelectedItem is not OwnerChoice choice) return;
            links.Enabled = false;
            candidate = null;
            model.ClearCandidate();
            progress.Value = 0;
            try
            {
                uint linkIndex = (uint)(choice.Id - 1);
                using var mesh = loadLinkMesh(choice.Name);
                using var options = new VhacdOptions {
                    MaxConvexHulls = 8, Resolution = 100000,
                    MinimumVolumePercentErrorAllowed = 2.5,
                    MaxRecursionDepth = 8, MaxNumVerticesPerCH = 32
                };
                if (!ConvexDecompositionSession.IsAvailable())
                    throw new InvalidOperationException("V-HACD backend is unavailable in this SDK installation.");
                using var session = new ConvexDecompositionSession(mesh, options);
                pendingDecomposition = session;
                status.Text = "Generating convex hulls…";
                using var result = await Task.Run(() => session.Compute());
                if (IsDisposed) return;
                if (!result.Succeeded()) {
                    status.Text = result.State + ": " + result.Diagnostic;
                    return;
                }
                var hulls = new List<CollisionDemoModel.Hull>();
                using (var source = result.Hulls)
                    foreach (var hull in source)
                    {
                        using (hull)
                        using (var vertices = hull.Vertices)
                        using (var indices = hull.Indices)
                        {
                            var values = new double[vertices.Count * 3];
                            for (int i = 0; i < vertices.Count; ++i)
                            {
                                using var point = vertices[i];
                                values[3 * i] = point.x;
                                values[3 * i + 1] = point.y;
                                values[3 * i + 2] = point.z;
                            }
                            hulls.Add(new CollisionDemoModel.Hull(values, indices.ToArray()));
                        }
                    }
                if (hulls.Count == 0) throw new InvalidOperationException("V-HACD returned no convex hulls.");
                candidate = (linkIndex, hulls);
                model.SetCandidate(linkIndex, hulls);
                status.Text = $"Preview ready: {hulls.Count} convex hulls. Confirm to use them in planning.";
                redraw();
            }
            catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
            finally {
                pendingDecomposition = null;
                if (!IsDisposed) links.Enabled = true;
            }
        }

        private void Confirm()
        {
            if (candidate is not { } next) return;
            model.SetLinkHulls(next.Index, next.Hulls);
            candidate = null;
            status.Text = $"Confirmed {next.Hulls.Count} Link convex hulls for collision planning.";
            redraw();
        }
        private void DeleteLink()
        {
            if (links.SelectedItem is not OwnerChoice choice) return;
            candidate = null;
            model.ClearCandidate();
            model.RemoveLinkHulls((uint)(choice.Id - 1));
            status.Text = "Link collision hulls deleted.";
            redraw();
        }

        private void RefreshOwners()
        {
            obstacleList.Items.Clear();
            foreach (var item in model.Obstacles) obstacleList.Items.Add(item);
            if (obstacleList.Items.Count > 0) obstacleList.SelectedIndex = 0;
            firstOwner.Items.Clear();
            secondOwner.Items.Clear();
            for (uint i = 0; i < body.GetLinkCount(); ++i)
            {
                var owner = new OwnerChoice((ulong)i + 1, model.LinkName(i));
                firstOwner.Items.Add(owner);
                secondOwner.Items.Add(owner);
            }
            foreach (var item in model.Obstacles)
            {
                var owner = new OwnerChoice(item.Id, item.Name);
                firstOwner.Items.Add(owner);
                secondOwner.Items.Add(owner);
            }
            if (firstOwner.Items.Count > 0) firstOwner.SelectedIndex = 0;
            if (secondOwner.Items.Count > 1) secondOwner.SelectedIndex = 1;
            RefreshPairs();
        }
        private void RefreshPairs()
        {
            pairList.Items.Clear();
            foreach (var pair in model.Ignored.Concat(model.Adjacent).Distinct()
                .OrderBy(item => item.First).ThenBy(item => item.Second))
                pairList.Items.Add(new PairChoice(pair,
                    model.OwnerName(pair.First) + " ↔ " + model.OwnerName(pair.Second) +
                    (model.Adjacent.Contains(pair) ? " (adjacent)" : "") +
                    (model.Ignored.Contains(pair) ? " — ignored" : " — checked")));
        }
        private static void SelectOwner(ComboBox box, ulong id)
        {
            for (int i = 0; i < box.Items.Count; ++i)
                if (box.Items[i] is OwnerChoice choice && choice.Id == id)
                {
                    box.SelectedIndex = i;
                    return;
                }
        }
        private void SetPair(bool ignore)
        {
            if (firstOwner.SelectedItem is not OwnerChoice first ||
                secondOwner.SelectedItem is not OwnerChoice second) return;
            if (!model.SetIgnored(first.Id, second.Id, ignore))
            {
                status.Text = first.Id == second.Id ? "Choose two different entities." :
                    "The pair already has that setting.";
                return;
            }
            RefreshPairs();
            status.Text = ignore ? "Collision pair ignored." : "Collision pair will be checked.";
        }
        private void LoadObstacle()
        {
            if (obstacleList.SelectedItem is not CollisionDemoModel.Obstacle item) return;
            updatingObstacle = true;
            var values = new[] { item.X, item.Y, item.Z, item.Width, item.Depth, item.Height };
            for (int i = 0; i < values.Length; ++i)
                obstacleValues[i].Value = Math.Clamp((decimal)values[i],
                    obstacleValues[i].Minimum, obstacleValues[i].Maximum);
            updatingObstacle = false;
        }
        private void SaveObstacle()
        {
            if (updatingObstacle || obstacleList.SelectedItem is not CollisionDemoModel.Obstacle item) return;
            item.X = (double)obstacleValues[0].Value;
            item.Y = (double)obstacleValues[1].Value;
            item.Z = (double)obstacleValues[2].Value;
            item.Width = (double)obstacleValues[3].Value;
            item.Depth = (double)obstacleValues[4].Value;
            item.Height = (double)obstacleValues[5].Value;
            model.RebuildPreview();
            redraw();
        }

        private async Task PlanTip()
        {
            if (pendingPlan != null) return;
            if (model.LinkHulls.Count == 0)
            {
                status.Text = "Confirm at least one Link V-HACD result before planning.";
                return;
            }
            player.Pause();
            using var session = new RobotSimulationSession(body, backends);
            pendingPlan = session;
            try
            {
                if (!session.IsValid()) throw new InvalidOperationException(session.GetDiagnostic());
                using var armIndex = new RobotArmIndex(0);
                using var arm = body.GetArm(armIndex);
                using var tipFrame = arm.GetTipFrameIndex();
                using var tipWorld = body.GetFrameTransform(tipFrame, true);
                using var offset = Matrix4d.makeTranslation(
                    (double)targetOffsets[0].Value, (double)targetOffsets[1].Value,
                    (double)targetOffsets[2].Value);
                using var targetWorld = offset.multiplied(tipWorld);
                using var robotModel = body.GetModel();
                using var rootIndex = robotModel.GetRootLinkIndex();
                using var rootWorld = body.GetLinkTransform(rootIndex, true);
                using var inverseRoot = rootWorld.inversed();
                using var targetInBody = inverseRoot.multiplied(targetWorld);
                using var target = new RobotSimulationTarget {
                    FrameIndex = tipFrame, TargetInBody = targetInBody
                };
                using var targets = new RobotSimulationTargetList();
                targets.Add(target);
                using var ik = session.SolveIk(targets);
                if (!ik.IsSuccess()) {
                    status.Text = "IK " + ik.Status + ": " + ik.Diagnostic;
                    return;
                }
                using var goals = new RobotJointPositionList();
                using var limits = new RobotMotionLimitList();
                using (var solved = ik.JointPositions)
                    foreach (var position in solved)
                    {
                        using (position)
                        using (var jointIndex = new RobotJointIndex(position.JointIndex))
                        using (var joint = body.GetJoint(jointIndex))
                        using (var goal = new RobotJointPosition {
                            JointIndex = jointIndex, Position = position.Position
                        })
                        using (var limit = new RobotMotionLimit {
                            JointIndex = jointIndex,
                            MaxVelocity = joint.GetMaxVelocity() > 0 ? joint.GetMaxVelocity() : 1,
                            MaxAcceleration = joint.GetMaxVelocity() > 0 ? 2 * joint.GetMaxVelocity() : 2
                        })
                        {
                            goals.Add(goal);
                            limits.Add(limit);
                        }
                    }
                using var request = session.CreatePlanningRequest(goals, limits);
                using (var options = request.Options) options.CollisionResolution = 0.05;
                model.ApplyTo(request);
                status.Text = "Planning a collision-free tip path…";
                using var result = await Task.Run(() => session.Plan(request));
                if (IsDisposed) return;
                if (!result.Succeeded()) {
                    using var planning = result.Planning;
                    status.Text = planning.Status + ": " + planning.Diagnostic;
                    return;
                }
                if (!player.Load(body, result)) throw new InvalidOperationException(player.GetDiagnostic());
                playTime.Maximum = (decimal)player.GetDuration();
                status.Text = $"Collision-free path ready: {player.GetDuration():F3} s.";
            }
            catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
            finally { pendingPlan = null; }
        }
    }
}
