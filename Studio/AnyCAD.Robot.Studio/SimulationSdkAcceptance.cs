using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AnyCAD.Robot.Studio
{
    public static class SimulationSdkAcceptance
    {
        public static int Run(string reportPath, string assetsRoot = null)
        {
            var report = new StringBuilder();
            try
            {
                using (var fkDefinition = new FkDefinition { LinkCount = 3 })
                {
                    using var fkWorld = Matrix4d.makeTranslation(10, 0, 0);
                    using var fkFirstRoot = new FkRoot { LinkIndex = 0, Transform = fkWorld };
                    using var fkSecondRoot = new FkRoot { LinkIndex = 2 };
                    using (var fkRoots = fkDefinition.Roots) { fkRoots.Add(fkFirstRoot); fkRoots.Add(fkSecondRoot); }
                    using var fkAxis = new Vector3d(1, 1, 0);
                    using var fkJoint = new FkJoint { ParentLinkIndex = 0, ChildLinkIndex = 1,
                        JointType = EnumRobotJointType.Prismatic, Axis = fkAxis };
                    using (var fkJoints = fkDefinition.Joints) fkJoints.Add(fkJoint);
                    using var fkOffset = Matrix4d.makeTranslation(0, 0, 2);
                    using var fkFrame = new FkFrame { LinkIndex = 1, Origin = fkOffset };
                    using (var fkFrames = fkDefinition.Frames) fkFrames.Add(fkFrame);
                    using var fkEvaluator = new FkEvaluator(fkDefinition);
                    using (var fkRoots = fkDefinition.Roots) fkRoots.Clear();
                    using var fkPositions = new DoubleList(); fkPositions.Add(Math.Sqrt(2));
                    using var fkResult = fkEvaluator.Evaluate(fkPositions);
                    Require(fkResult.Succeeded(), fkResult.Diagnostic);
                    using Matrix4dList fkTransforms = fkResult.FrameTransforms;
                    Require(fkTransforms.Count == 1, "Direct FK Frame collection");
                    using var fkTransform = fkTransforms[0];
                    using var fkOrigin = new Vector3d(0, 0, 0);
                    fkOrigin.applyMatrix4d(fkTransform);
                    Require(Math.Abs(fkOrigin.x - 11) < 1e-10 && Math.Abs(fkOrigin.y - 1) < 1e-10 &&
                        Math.Abs(fkOrigin.z - 2) < 1e-10, "Direct FK forest coordinates");
                    fkPositions.Clear();
                    using var fkInvalid = fkEvaluator.Evaluate(fkPositions);
                    Require(fkInvalid.Status == EnumFkStatus.InvalidPositions, "Reject incomplete FK state");
                    report.AppendLine("PASS direct FK forest / Foundation matrices / snapshot / invalid input");
                }
                using var mesh = new CollisionTriangleMesh();
                using (Double3List vertices = mesh.Vertices)
                {
                    foreach (var xyz in new[] { new[] {0.0,0,0}, new[] {1.0,0,0}, new[] {0.0,1,0}, new[] {0.0,0,1} })
                    {
                        using var vertex = new Vector3d(xyz[0], xyz[1], xyz[2]);
                        vertices.Add(vertex);
                    }
                }
                using (Uint32List indices = mesh.Indices)
                    foreach (uint index in new uint[] {0,2,1,0,1,3,1,2,3,2,0,3}) indices.Add(index);
                using var decompositionOptions = new VhacdOptions { Resolution = 10000 };
                using var decomposition = new ConvexDecompositionSession(mesh, decompositionOptions);
                using var cancelledDecomposition = new ConvexDecompositionSession(mesh, decompositionOptions);
                using (var vertices = mesh.Vertices) vertices.Clear(); // Constructor snapshots its input.
                var compute = Task.Run(() => decomposition.Compute());
                double previousProgress = 0;
                while (!compute.IsCompleted)
                {
                    double progress = decomposition.GetProgress();
                    // Always join before disposing the native session, including failed assertions.
                    if (progress < previousProgress || progress > 1)
                    {
                        using var ignored = compute.GetAwaiter().GetResult();
                        Require(false, "Monotonic decomposition progress");
                    }
                    previousProgress = progress;
                    System.Threading.Thread.Sleep(1);
                }
                using var decompositionResult = compute.GetAwaiter().GetResult();
                if (ConvexDecompositionSession.IsAvailable())
                {
                    Require(decompositionResult.Succeeded(), decompositionResult.Diagnostic);
                    using CollisionTriangleMeshList hulls = decompositionResult.Hulls;
                    Require(hulls.Count > 0, "Managed convex hull collection");
                    using var hull = hulls[0];
                    Require(hull.IsValid(), "Managed convex mesh round trip");
                    Require(decomposition.GetProgress() == 1, "Completed decomposition progress");
                }
                else Require(decompositionResult.State == EnumConvexDecompositionState.BackendUnavailable,
                    decompositionResult.Diagnostic);
                cancelledDecomposition.Cancel();
                using var cancelledResult = Task.Run(() => cancelledDecomposition.Compute()).GetAwaiter().GetResult();
                Require(cancelledResult.State == EnumConvexDecompositionState.Cancelled, "Managed cancellation");
                report.AppendLine("PASS direct convex decomposition / caller Task.Run / cancellation");
                var model = new RobotModel();
                using var rootLink = new RobotLink();
                using var tipLink = new RobotLink();
                using var root = model.AddLink(rootLink);
                using var tip = model.AddLink(tipLink);
                model.SetRootLink(root);
                using var joint = new RobotJointEdge();
                joint.SetParentLinkIndex(root);
                joint.SetChildLinkIndex(tip);
                joint.SetJointType(EnumRobotJointType.Prismatic);
                using var axis = new Vector3d(1, 0, 0);
                joint.SetAxis(axis);
                joint.SetMinValue(-2);
                joint.SetMaxValue(2);
                using var jointIndex = model.AddJoint(joint);
                using var frame = new RobotFrame();
                frame.SetLinkIndex(tip);
                using var frameIndex = model.AddFrame(frame);
                using (var copiedModel = new RobotModel(model))
                {
                    Require(copiedModel.GetLinkCount() == 2, "RobotModel copy keeps topology");
                    copiedModel.SetName("managed-copy");
                    Require(model.GetName() != copiedModel.GetName(), "RobotModel copy has independent value state");
                }
                using var body = RobotBody.Create(model);
                Require(body != null, "Create Element-free RobotBody");
                model.SetName("managed-shared");
                Require(model.GetName() == "managed-shared", "Create keeps the input model proxy alive");
                using (var sharedModel = body.GetModel())
                {
                    Require(sharedModel.GetName() == "managed-shared", "Body exposes the shared model");
                    sharedModel.SetName("body-shared");
                }
                Require(model.GetName() == "body-shared", "Model mutations are shared");
                model.Dispose();
                Require(body.GetLinkCount() == 2, "Body retains the model after caller disposal");
                foreach (var name in new[] { "KDL", "LMA", "RL" })
                {
                    using var info = SimulationBackends.Describe(EnumSimulationBackendKind.Ik, name);
                    Require(info.Available, name + " registered");
                    using var selected = new BackendSelection { Ik = name, Planner = "OMPL", Collision = "Bullet" };
                    using var session = new RobotSimulationSession(body, selected);
                    using var transform = new Matrix4d(1);
                    using var offset = new Vector3d(0.5, 0, 0);
                    transform.translate(offset);
                    using var target = new RobotSimulationTarget { FrameIndex = frameIndex, TargetInBody = transform };
                    using var targets = new RobotSimulationTargetList();
                    targets.Add(target);
                    using var result = session.SolveIk(targets);
                    Require(result.IsSuccess(), name + " IK: " + result.Diagnostic);
                    Require(result.Backend == name, "Actual IK dispatch");
                    report.AppendLine("PASS IK " + result.Backend + " / " + result.Algorithm);
                }
                foreach (var curve in new[] { EnumJointTrajectoryCurve.CubicStopToStop, EnumJointTrajectoryCurve.QuinticStopToStop })
                {
                    using var path = new DoubleList2();
                    using var start = new DoubleList();
                    using var end = new DoubleList();
                    start.Add(0); end.Add(1);
                    path.Add(start); path.Add(end);
                    using var limits = new JointTrajectoryLimitList();
                    using var limit = new JointTrajectoryLimit { MaxVelocity = 1, MaxAcceleration = 2 };
                    limits.Add(limit);
                    using var options = new JointTrajectoryOptions { Curve = curve, MinimumDuration = 4 };
                    using var timed = JointTrajectoryTiming.Parameterize(path, limits, options);
                    Require(timed.Succeeded, timed.Diagnostic);
                    using var trajectory = timed.Trajectory;
                    Require(trajectory.Curve == curve, "Timing curve survives managed round trip");
                    using var sample = JointTrajectorySampler.Sample(trajectory, 1);
                    Require(sample.Succeeded, sample.Diagnostic);
                    using DoubleList positions = sample.Positions;
                    double expected = curve == EnumJointTrajectoryCurve.CubicStopToStop ? 0.15625 : 0.103515625;
                    Require(Math.Abs(positions[0] - expected) < 1e-12, "Direct SDK quarter-time sample");
                    report.AppendLine("PASS direct trajectory timing/sampling " + curve);
                }
                foreach (var planner in new[] { "OMPL" })
                    foreach (var collision in new[] { "FCL" })
                    foreach (var curve in new[] { EnumJointTrajectoryCurve.CubicStopToStop, EnumJointTrajectoryCurve.QuinticStopToStop })
                    {
                        using var selected = new BackendSelection { Ik = "KDL", Planner = planner, Collision = collision };
                        using var session = new RobotSimulationSession(body, selected);
                        using var goals = new RobotJointPositionList();
                        using var goal = new RobotJointPosition { JointIndex = jointIndex, Position = 1 };
                        goals.Add(goal);
                        using var limits = new RobotMotionLimitList();
                        using var limit = new RobotMotionLimit { JointIndex = jointIndex, MaxVelocity = 1, MaxAcceleration = 2 };
                        limits.Add(limit);
                        using var request = session.CreatePlanningRequest(goals, limits);
                        using (var timing = request.Timing) timing.Curve = curve;
                        using var result = session.Plan(request);
                        using var planning = result.Planning;
                        Require(result.Succeeded(), planner + "/" + collision + ": " + planning.Diagnostic);
                        Require(result.Curve == curve, "Planned curve survives managed round trip");
                        using (AnyCAD.Foundation.DoubleList2 path = planning.Path)
                        {
                            Require(path.Count >= 2, "Foundation path collection");
                            using AnyCAD.Foundation.DoubleList start = path[0];
                            using AnyCAD.Foundation.DoubleList end = path[path.Count - 1];
                            Require(start.Count == 1 && end.Count == 1, "Path joint dimensions");
                            Require(Math.Abs(start[0]) < 1e-8 && Math.Abs(end[0] - 1) < 1e-8,
                                "Read planner endpoints through Foundation collections");
                        }
                        using var player = new RobotTrajectoryPlayer();
                        Require(player.Load(body, result), player.GetDiagnostic());
                        Require(player.Play(), "Play");
                        Require(player.Tick(player.GetDuration() / 2), "Tick");
                        player.Pause();
                        double paused = body.GetJointPosition(jointIndex);
                        Require(player.Tick(10) && body.GetJointPosition(jointIndex) == paused, "Pause keeps state");
                        Require(player.Seek(player.GetDuration()), "Seek");
                        Require(Math.Abs(body.GetJointPosition(jointIndex) - 1) < 1e-8, "Final position");
                        Require(player.Stop(), "Stop");
                        Require(Math.Abs(body.GetJointPosition(jointIndex)) < 1e-8, "Return to start");
                        session.Cancel();
                        using var cancelled = session.Plan(request);
                        using var cancelledPlanning = cancelled.Planning;
                        Require(cancelledPlanning.Status == EnumJointSpacePlanningStatus.Cancelled, "Cancellation");
                        report.AppendLine("PASS planning/Foundation collections/playback " + planner + " / " + collision + " / " + curve);
                    }
                using (var selected = new BackendSelection { Ik = "not-installed" })
                using (var session = new RobotSimulationSession(body, selected))
                using (var targets = new RobotSimulationTargetList())
                using (var transform = new Matrix4d(1))
                using (var target = new RobotSimulationTarget { FrameIndex = frameIndex, TargetInBody = transform })
                {
                    targets.Add(target);
                    using var result = session.SolveIk(targets);
                    Require(result.Status == EnumTreeIkStatus.BackendUnavailable, "No backend fallback");
                }
                report.AppendLine("PASS missing backend rejection");
                using (var continuousModel = new RobotModel())
                using (var continuousRoot = new RobotLink())
                using (var continuousTip = new RobotLink())
                {
                    using var rootIndex = continuousModel.AddLink(continuousRoot);
                    using var tipIndex = continuousModel.AddLink(continuousTip);
                    continuousModel.SetRootLink(rootIndex);
                    using var continuousJoint = new RobotJointEdge();
                    continuousJoint.SetParentLinkIndex(rootIndex);
                    continuousJoint.SetChildLinkIndex(tipIndex);
                    continuousJoint.SetJointType(EnumRobotJointType.Continuous);
                    using var axisZ = new Vector3d(0, 0, 1);
                    continuousJoint.SetAxis(axisZ);
                    using var continuousJointIndex = continuousModel.AddJoint(continuousJoint);
                    using var continuousFrame = new RobotFrame();
                    continuousFrame.SetLinkIndex(tipIndex);
                    using var continuousFrameIndex = continuousModel.AddFrame(continuousFrame);
                    using var continuousArm = new RobotArm();
                    continuousArm.SetBaseLinkIndex(rootIndex);
                    continuousArm.SetTipFrameIndex(continuousFrameIndex);
                    using var continuousArmIndex = continuousModel.AddArm(continuousArm);
                    using var continuousBody = RobotBody.Create(continuousModel);
                    Require(continuousBody != null, "Continuous RobotBody");
                    var controller = new RobotControler(continuousBody);
                    Require(controller.AddJointPosition(0, 0, 7.0),
                        "Continuous jog accepts unbounded angle (arms=" + controller.ArmCount +
                        ", joints=" + (controller.ArmCount > 0 ? controller.GetJointCount(0) : 0) +
                        ", type=" + controller.GetJointType(0, 0) +
                        ", position=" + controller.GetJointPosition(0, 0) + ")");
                    Require(Math.Abs(controller.GetJointPosition(0, 0) - 7.0) < 1e-8,
                        "Continuous jog does not clamp at 2π");
                    report.AppendLine("PASS Continuous joint unbounded jog");
                }
                if (assetsRoot != null)
                {
                    foreach (var sample in new[]
                    {
                        (Name: "Kr500", Links: 8u, Joints: 7u, Arms: 1u),
                        (Name: "3A", Links: 6u, Joints: 5u, Arms: 1u),
                        (Name: "dual_slide", Links: 5u, Joints: 4u, Arms: 2u),
                        (Name: "shared_gantry_dual_6r", Links: 16u, Joints: 15u, Arms: 2u)
                    })
                    {
                        var fileName = System.IO.Path.Combine(assetsRoot, sample.Name, "index.urdf");
                        Require(File.Exists(fileName), "Missing URDF asset: " + fileName);
                        using var urdfPath = new AnyCAD.Foundation.Path(fileName);
                        using var sampleBody = RobotIO.Load(urdfPath);
                        Require(sampleBody != null, "Cannot load URDF: " + sample.Name);
                        Require(sampleBody.GetLinkCount() == sample.Links &&
                            sampleBody.GetJointCount() == sample.Joints &&
                            sampleBody.GetArmCount() == sample.Arms,
                            "URDF topology: " + sample.Name);
                        sampleBody.UpdateFrames();
                        for (uint armNumber = 0; armNumber < sample.Arms; ++armNumber)
                        {
                            using var armIndex = new RobotArmIndex(armNumber);
                            using var arm = sampleBody.GetArm(armIndex);
                            Require(arm != null, "URDF arm: " + sample.Name);
                            using var tipIndex = arm.GetTipFrameIndex();
                            using var tipFrame = sampleBody.GetFrameTransform(tipIndex, false);
                            using var tipOrigin = new Vector3d(0, 0, 0);
                            tipOrigin.applyMatrix4d(tipFrame);
                            Require(double.IsFinite(tipOrigin.x) && double.IsFinite(tipOrigin.y) &&
                                double.IsFinite(tipOrigin.z), "URDF FK: " + sample.Name);
                        }
                        report.AppendLine("PASS URDF load/FK " + sample.Name);
                    }
                }
                File.WriteAllText(reportPath, report.ToString());
                return 0;
            }
            catch (Exception error)
            {
                report.AppendLine("FAIL " + error);
                File.WriteAllText(reportPath, report.ToString());
                return 1;
            }
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
