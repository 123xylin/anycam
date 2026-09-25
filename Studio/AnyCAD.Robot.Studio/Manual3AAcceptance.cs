using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnyCAD.Robot.Studio
{
    public static class Manual3AAcceptance
    {
        public static int Run(string reportPath, string modelRoot)
        {
            var report = new StringBuilder();
            try
            {
                var directory = System.IO.Path.Combine(modelRoot, "3A");
                using var manual = Manual3AFactory.Create(directory);
                using var file = new AnyCAD.Foundation.Path(
                    System.IO.Path.Combine(directory, "index.urdf"));
                using var imported = RobotIO.Load(file);
                Check(manual != null && imported != null, "Both 3A models load");
                Check(manual.GetLinkCount() == imported.GetLinkCount() &&
                    manual.GetLinkCount() == 6, "Six Links");
                Check(manual.GetJointCount() == imported.GetJointCount() &&
                    manual.GetJointCount() == 5, "Five Joints");
                Check(manual.GetFrameCount() == 1 && imported.GetFrameCount() == 1 &&
                    manual.GetArmCount() == 1 && imported.GetArmCount() == 1,
                    "One tip Frame and Arm");
                for (uint i = 0; i < manual.GetLinkCount(); ++i)
                {
                    using var index = new RobotLinkIndex(i);
                    using var a = manual.GetLink(index);
                    using var b = imported.GetLink(index);
                    Check(a.GetName() == b.GetName(), "Link name " + i);
                    using var visuals = a.GetVisuals();
                    Check(visuals.Count == (i == 5 ? 0 : 1), "BREP visual " + i);
                    if (i == 5) continue;
                    using var node = visuals[0];
                    using var brep = BrepSceneNode.Cast(node);
                    Check(brep != null && brep.GetFaceCount() > 0,
                        "Rendered BREP faces for " + a.GetName());
                    using var mesh = Manual3AFactory.LoadLinkMesh(directory, a.GetName());
                    Check(mesh.IsValid(), "Link-local BREP mesh " + a.GetName());
                    using var reference = UrdfVisualMesh.Load(
                        System.IO.Path.Combine(directory, "index.urdf"), a.GetName());
                    CompareBounds(mesh, reference, a.GetName());
                }
                report.AppendLine("PASS six Links, five BREP visuals and matching local mesh bounds");

                for (uint i = 0; i < manual.GetJointCount(); ++i)
                {
                    using var index = new RobotJointIndex(i);
                    using var a = manual.GetJoint(index);
                    using var b = imported.GetJoint(index);
                    Check(a.GetName() == b.GetName() && a.GetJointType() == b.GetJointType(),
                        "Joint name/type " + i);
                    using var ap = a.GetParentLinkIndex();
                    using var bp = b.GetParentLinkIndex();
                    using var ac = a.GetChildLinkIndex();
                    using var bc = b.GetChildLinkIndex();
                    Check(ap.GetValue() == bp.GetValue() && ac.GetValue() == bc.GetValue(),
                        "Joint topology " + i);
                    using var ao = a.GetOrigin();
                    using var bo = b.GetOrigin();
                    CompareMatrix(ao, bo, "Joint origin " + i);
                    if (a.GetJointType() == EnumRobotJointType.Fixed) continue;
                    using var axisA = a.GetAxis();
                    using var axisB = b.GetAxis();
                    Near(axisA.x, axisB.x, "Axis X");
                    Near(axisA.y, axisB.y, "Axis Y");
                    Near(axisA.z, axisB.z, "Axis Z");
                    Near(a.GetMinValue(), b.GetMinValue(), "Joint min");
                    Near(a.GetMaxValue(), b.GetMaxValue(), "Joint max");
                    Near(a.GetMaxVelocity(), b.GetMaxVelocity(), "Joint velocity");
                }
                using (var index = new RobotFrameIndex(0))
                using (var a = manual.GetFrame(index))
                using (var b = imported.GetFrame(index))
                    Check(a.GetName() == b.GetName(), "Tip Frame name");
                using (var index = new RobotArmIndex(0))
                using (var a = manual.GetArm(index))
                using (var b = imported.GetArm(index))
                    Check(a.GetName() == b.GetName(), "Arm name");
                CompareFk(manual, imported, "home");
                double[] positions = { 120, -75, 40, 90 };
                for (uint i = 0; i < positions.Length; ++i)
                {
                    using var index = new RobotJointIndex(i);
                    Check(manual.SetJointPosition(index, positions[i]), "Manual joint state " + i);
                    Check(imported.SetJointPosition(index, positions[i]), "URDF joint state " + i);
                }
                manual.UpdateFrames();
                imported.UpdateFrames();
                CompareFk(manual, imported, "nonzero");
                report.AppendLine("PASS Joint topology, limits and home/nonzero FK");

                using var meshT = Manual3AFactory.LoadLinkMesh(directory, "T");
                using var options = new VhacdOptions {
                    MaxConvexHulls = 8, Resolution = 100000,
                    MinimumVolumePercentErrorAllowed = 2.5,
                    MaxRecursionDepth = 8, MaxNumVerticesPerCH = 32
                };
                using var decomposition = new ConvexDecompositionSession(meshT, options);
                using var result = decomposition.Compute();
                Check(result.Succeeded(), "3A BREP V-HACD: " + result.Diagnostic);
                var hulls = new List<CollisionDemoModel.Hull>();
                using (var source = result.Hulls)
                    foreach (var hull in source)
                    {
                        using (hull)
                        using (var vertices = hull.Vertices)
                        using (var indices = hull.Indices)
                        {
                            var coordinates = new double[vertices.Count * 3];
                            for (int i = 0; i < vertices.Count; ++i)
                            {
                                using var point = vertices[i];
                                coordinates[3 * i] = point.x;
                                coordinates[3 * i + 1] = point.y;
                                coordinates[3 * i + 2] = point.z;
                            }
                            hulls.Add(new CollisionDemoModel.Hull(coordinates, indices.ToArray()));
                        }
                    }
                Check(hulls.Count > 0, "3A BREP produced no convex hulls");
                var scene = new CollisionDemoModel(manual, false);
                scene.SetLinkHulls(4, hulls);
                scene.AddObstacle();
                using var session = new RobotSimulationSession(manual);
                Check(session.IsValid(), "3A simulation session: " + session.GetDiagnostic());
                using var jointIndex = new RobotJointIndex(0);
                using var goals = new RobotJointPositionList();
                using var goal = new RobotJointPosition { JointIndex = jointIndex, Position = 100 };
                goals.Add(goal);
                using var limits = new RobotMotionLimitList();
                using var limit = new RobotMotionLimit {
                    JointIndex = jointIndex, MaxVelocity = 200, MaxAcceleration = 400
                };
                limits.Add(limit);
                using var request = session.CreatePlanningRequest(goals, limits);
                scene.ApplyTo(request);
                using (var collision = request.CollisionScene)
                using (var bodies = collision.Bodies)
                using (var bindings = request.CollisionBindings)
                    Check(bodies.Count == 2 && bindings.Count == 1,
                        "BREP hull and scene obstacle enter the same planning request");
                using (var armIndex = new RobotArmIndex(0))
                using (var arm = manual.GetArm(armIndex))
                using (var tip = arm.GetTipFrameIndex())
                using (var currentTip = manual.GetFrameTransform(tip, false))
                using (var target = new RobotSimulationTarget {
                    FrameIndex = tip, TargetInBody = currentTip
                })
                using (var targets = new RobotSimulationTargetList())
                {
                    targets.Add(target);
                    using var ik = session.SolveIk(targets);
                    Check(ik.IsSuccess(), "3A IK: " + ik.Status + " " + ik.Diagnostic);
                }
                using (var planned = session.Plan(request))
                using (var details = planned.Planning)
                {
                    Check(planned.Succeeded(), "3A collision-free plan: " +
                        details.Status + " " + details.Diagnostic);
                    using var player = new RobotTrajectoryPlayer();
                    Check(player.Load(manual, planned), "3A playback load: " +
                        player.GetDiagnostic());
                    Check(player.Play(), "3A playback start");
                    Check(player.Tick(player.GetDuration() / 2), "3A playback tick");
                    Check(player.Stop(), "3A playback stop");
                }
                report.AppendLine("PASS 3A BREP V-HACD hulls=" + hulls.Count +
                    ", IK, collision-free planning and playback");
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

        private static void CompareBounds(CollisionTriangleMesh a, CollisionTriangleMesh b,
            string name)
        {
            static double[] Bounds(CollisionTriangleMesh mesh)
            {
                var result = new[] { double.PositiveInfinity, double.PositiveInfinity,
                    double.PositiveInfinity, double.NegativeInfinity,
                    double.NegativeInfinity, double.NegativeInfinity };
                using var vertices = mesh.Vertices;
                foreach (var point in vertices)
                    using (point)
                    {
                        result[0] = Math.Min(result[0], point.x);
                        result[1] = Math.Min(result[1], point.y);
                        result[2] = Math.Min(result[2], point.z);
                        result[3] = Math.Max(result[3], point.x);
                        result[4] = Math.Max(result[4], point.y);
                        result[5] = Math.Max(result[5], point.z);
                    }
                return result;
            }
            var actual = Bounds(a);
            var expected = Bounds(b);
            for (int i = 0; i < 6; ++i)
                Check(Math.Abs(actual[i] - expected[i]) < 2,
                    name + " bound " + i + ": " + actual[i] + " vs " + expected[i]);
        }

        private static void CompareFk(RobotBody a, RobotBody b, string state)
        {
            for (uint i = 0; i < a.GetLinkCount(); ++i)
            {
                using var index = new RobotLinkIndex(i);
                using var actual = a.GetLinkTransform(index, false);
                using var expected = b.GetLinkTransform(index, false);
                CompareMatrix(actual, expected, state + " Link " + i);
            }
            using var frame = new RobotFrameIndex(0);
            using var actualTip = a.GetFrameTransform(frame, false);
            using var expectedTip = b.GetFrameTransform(frame, false);
            CompareMatrix(actualTip, expectedTip, state + " tip");
        }

        private static void CompareMatrix(Matrix4d actual, Matrix4d expected, string label)
        {
            foreach (var xyz in new[] { (0.0, 0.0, 0.0), (1.0, 0.0, 0.0),
                (0.0, 1.0, 0.0), (0.0, 0.0, 1.0) })
            {
                using var a = new Vector3d(xyz.Item1, xyz.Item2, xyz.Item3);
                using var b = new Vector3d(xyz.Item1, xyz.Item2, xyz.Item3);
                a.applyMatrix4d(actual);
                b.applyMatrix4d(expected);
                Near(a.x, b.x, label + " X");
                Near(a.y, b.y, label + " Y");
                Near(a.z, b.z, label + " Z");
            }
        }

        private static void Near(double actual, double expected, string label)
            => Check(Math.Abs(actual - expected) < 1e-5,
                label + ": " + actual + " vs " + expected);

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
