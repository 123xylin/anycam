using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RapidRobot
{
    internal static class CollisionDemoAcceptance
    {
        internal static int Run(string reportPath, string modelRoot)
        {
            var report = new StringBuilder();
            try
            {
                string urdf = System.IO.Path.Combine(modelRoot, "Kr500", "index.urdf");
                using var path = new AnyCAD.Foundation.Path(urdf);
                using var body = RobotIO.Load(path);
                Check(body != null, "Kr500 URDF load");
                using (var axis2 = new RobotJointIndex(1))
                using (var axis3 = new RobotJointIndex(2))
                {
                    Check(body.SetJointPosition(axis2, -0.45), "Kr500 AXIS2 demo pose");
                    Check(body.SetJointPosition(axis3, 0.6), "Kr500 AXIS3 demo pose");
                    body.UpdateFrames();
                }
                var demo = new CollisionDemoModel(body, false);
                foreach (var name in new[] { "AXIS2", "AXIS3" })
                {
                    uint index = FindLink(body, name);
                    using var mesh = UrdfVisualMesh.Load(urdf, name);
                    using var options = new VhacdOptions {
                        MaxConvexHulls = 8, Resolution = 100000,
                        MinimumVolumePercentErrorAllowed = 2.5,
                        MaxRecursionDepth = 8, MaxNumVerticesPerCH = 32
                    };
                    using var session = new ConvexDecompositionSession(mesh, options);
                    using var result = session.Compute();
                    Check(result.Succeeded(), name + ": " + result.Diagnostic);
                    var hulls = new List<CollisionDemoModel.Hull>();
                    using (var output = result.Hulls)
                        foreach (var hull in output)
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
                    Check(hulls.Count > 0, name + " produced no convex hulls");
                    demo.SetLinkHulls(index, hulls);
                    report.AppendLine("PASS Kr500 " + name + " V-HACD hulls=" + hulls.Count);
                }
                var obstacle = demo.AddObstacle();
                using (var armIndex = new RobotArmIndex(0))
                using (var arm = body.GetArm(armIndex))
                using (var tipFrame = arm.GetTipFrameIndex())
                using (var tipWorld = body.GetFrameTransform(tipFrame, true))
                {
                    using var tipPoint = new Vector3d(0, 0, 0);
                    tipPoint.applyMatrix4d(tipWorld);
                    report.AppendLine($"Start tip: {tipPoint.x:F3}, {tipPoint.y:F3}, {tipPoint.z:F3}");
                }
                using var robotSession = new RobotSimulationSession(body);
                using var jointIndex = new RobotJointIndex(0);
                using var goals = new RobotJointPositionList();
                using var goal = new RobotJointPosition { JointIndex = jointIndex, Position = 0.2 };
                goals.Add(goal);
                using var limits = new RobotMotionLimitList();
                using var limit = new RobotMotionLimit {
                    JointIndex = jointIndex, MaxVelocity = 1, MaxAcceleration = 2
                };
                limits.Add(limit);
                using var request = robotSession.CreatePlanningRequest(goals, limits);
                demo.ApplyTo(request);
                using (var scene = request.CollisionScene)
                using (var bodies = scene.Bodies)
                using (var bindings = request.CollisionBindings)
                using (var pairs = scene.IgnoredPairs)
                {
                    Check(bodies.Count == 3 && bindings.Count == 2, "Link and environment bodies");
                    Check(pairs.Count == 1, "Adjacent Link pair starts ignored");
                }
                uint first = FindLink(body, "AXIS2"), second = FindLink(body, "AXIS3");
                Check(demo.SetIgnored((ulong)first + 1, (ulong)second + 1, false),
                    "Remove adjacent exemption");
                demo.ApplyTo(request);
                using (var scene = request.CollisionScene)
                using (var pairs = scene.IgnoredPairs)
                    Check(pairs.Count == 0, "Adjacent pair is now checked");
                Check(demo.SetIgnored((ulong)first + 1, (ulong)second + 1, true),
                    "Restore adjacent exemption");
                var adjacentCheck = new CollisionDemoModel(body, false);
                adjacentCheck.SetLinkHulls(first, new List<CollisionDemoModel.Hull> {
                    WorldCube(body, first)
                });
                adjacentCheck.SetLinkHulls(second, new List<CollisionDemoModel.Hull> {
                    WorldCube(body, second)
                });
                using (var pairRequest = robotSession.CreatePlanningRequest(goals, limits))
                {
                    adjacentCheck.ApplyTo(pairRequest);
                    using (var ignoredResult = robotSession.Plan(pairRequest))
                        Check(ignoredResult.Succeeded(), "Default adjacent exemption permits motion");
                    Check(adjacentCheck.SetIgnored((ulong)first + 1, (ulong)second + 1, false),
                        "Synthetic adjacent exemption removal");
                    adjacentCheck.ApplyTo(pairRequest);
                    using (var checkedResult = robotSession.Plan(pairRequest))
                    using (var checkedPlanning = checkedResult.Planning)
                        Check(checkedPlanning.Status == EnumJointSpacePlanningStatus.InvalidStart,
                            "Removing adjacent exemption detects overlapping Link bodies");
                }
                report.AppendLine("PASS adjacent Link exemption changes collision detection");
                List<CollisionDemoModel.Hull> axis6Hulls;
                using (var mesh = UrdfVisualMesh.Load(urdf, "AXIS6"))
                using (var options = new VhacdOptions {
                    MaxConvexHulls = 8, Resolution = 100000,
                    MinimumVolumePercentErrorAllowed = 2.5,
                    MaxRecursionDepth = 8, MaxNumVerticesPerCH = 32
                })
                using (var decomposition = new ConvexDecompositionSession(mesh, options))
                using (var result = decomposition.Compute())
                {
                    Check(result.Succeeded(), "AXIS6: " + result.Diagnostic);
                    var hulls = new List<CollisionDemoModel.Hull>();
                    using (var output = result.Hulls)
                        foreach (var hull in output)
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
                    axis6Hulls = hulls;
                    demo.SetLinkHulls(FindLink(body, "AXIS6"), hulls);
                    report.AppendLine("PASS Kr500 AXIS6 V-HACD hulls=" + hulls.Count);
                }
                using (var armIndex = new RobotArmIndex(0))
                using (var arm = body.GetArm(armIndex))
                using (var tipFrame = arm.GetTipFrameIndex())
                using (var tipWorld = body.GetFrameTransform(tipFrame, true))
                using (var offset = Matrix4d.makeTranslation(-450, 0, 0))
                using (var targetWorld = offset.multiplied(tipWorld))
                using (var robotModel = body.GetModel())
                using (var rootIndex = robotModel.GetRootLinkIndex())
                using (var rootWorld = body.GetLinkTransform(rootIndex, true))
                using (var inverseRoot = rootWorld.inversed())
                using (var targetInBody = inverseRoot.multiplied(targetWorld))
                using (var target = new RobotSimulationTarget {
                    FrameIndex = tipFrame, TargetInBody = targetInBody
                })
                using (var targets = new RobotSimulationTargetList())
                {
                    targets.Add(target);
                    using var ik = robotSession.SolveIk(targets);
                    Check(ik.IsSuccess(), "Kr500 tip IK: " + ik.Status + " " + ik.Diagnostic);
                    {
                        using var tipGoals = new RobotJointPositionList();
                        using var tipLimits = new RobotMotionLimitList();
                        using (var solved = ik.JointPositions)
                            foreach (var position in solved)
                            {
                                using (position)
                                using (var jointId = new RobotJointIndex(position.JointIndex))
                                using (var tipGoal = new RobotJointPosition {
                                    JointIndex = jointId, Position = position.Position
                                })
                                using (var tipLimit = new RobotMotionLimit {
                                    JointIndex = jointId, MaxVelocity = 1, MaxAcceleration = 2
                                })
                                { tipGoals.Add(tipGoal); tipLimits.Add(tipLimit); }
                            }
                        using var tipRequest = robotSession.CreatePlanningRequest(tipGoals, tipLimits);
                        using (var options = tipRequest.Options) options.CollisionResolution = 0.05;
                        using var middleGoals = new RobotJointPositionList();
                        foreach (var position in tipGoals)
                        {
                            using (position)
                            using (var jointId = position.JointIndex)
                            using (var middle = new RobotJointPosition {
                                JointIndex = jointId,
                                Position = (body.GetJointPosition(jointId) + position.Position) / 2
                            }) middleGoals.Add(middle);
                        }
                        using var middleRequest = robotSession.CreatePlanningRequest(middleGoals, tipLimits);
                        using (var options = middleRequest.Options) options.CollisionResolution = 0.05;
                        demo.ApplyTo(middleRequest);
                        using (var middleResult = robotSession.Plan(middleRequest))
                        using (var middlePlanning = middleResult.Planning)
                            Check(middlePlanning.Status == EnumJointSpacePlanningStatus.InvalidGoal,
                                "The straight joint path must cross the obstacle");
                        demo.ApplyTo(tipRequest);
                        using (var tipResult = robotSession.Plan(tipRequest))
                        using (var tipPlanning = tipResult.Planning)
                        using (var pathPoints = tipPlanning.Path)
                        {
                            Check(tipResult.Succeeded(), "Obstacle path: " +
                                tipPlanning.Status + " " + tipPlanning.Diagnostic);
                            Check(pathPoints.Count > 2, "Obstacle must force a detour");
                            int samples = CheckPathSamples(robotSession, demo, pathPoints,
                                tipGoals, tipLimits);
                            using var player = new RobotTrajectoryPlayer();
                            Check(player.Load(body, tipResult), "Kr500 trajectory load: " +
                                player.GetDiagnostic());
                            Check(player.Play(), "Kr500 trajectory play");
                            Check(player.Tick(player.GetDuration() / 2),
                                "Kr500 trajectory midpoint playback");
                            player.Pause();
                            Check(player.Seek(player.GetDuration()), "Kr500 trajectory seek");
                            Check(player.Stop(), "Kr500 trajectory stop");
                            report.AppendLine("PASS Kr500 tip IK and collision-free detour, " +
                                pathPoints.Count + " waypoints, " + samples + " sampled states");
                        }
                        demo.RemoveObstacle(obstacle);
                        demo.ApplyTo(tipRequest);
                        using (var directResult = robotSession.Plan(tipRequest))
                        using (var directPlanning = directResult.Planning)
                        using (var directPath = directPlanning.Path)
                            Check(directResult.Succeeded() && directPath.Count == 2,
                                "Removing the box restores the direct path");
                        var replacement = demo.AddObstacle();
                        demo.RemoveLinkHulls(FindLink(body, "AXIS6"));
                        demo.ApplyTo(tipRequest);
                        using (var removedResult = robotSession.Plan(tipRequest))
                        using (var removedPlanning = removedResult.Planning)
                        using (var removedPath = removedPlanning.Path)
                            Check(removedResult.Succeeded() && removedPath.Count == 2,
                                "Deleting AXIS6 hulls changes collision planning");
                        demo.SetLinkHulls(FindLink(body, "AXIS6"), axis6Hulls);
                        demo.ApplyTo(tipRequest);
                        using (var restoredResult = robotSession.Plan(tipRequest))
                        using (var restoredPlanning = restoredResult.Planning)
                        using (var restoredPath = restoredPlanning.Path)
                            Check(restoredResult.Succeeded() && restoredPath.Count > 2,
                                "Regenerating AXIS6 hulls restores the detour");
                        demo.RemoveObstacle(replacement);
                        report.AppendLine("PASS obstacle edit and Link hull delete/regenerate affect planning");
                    }
                }
                demo.ApplyTo(request);
                using (var scene = request.CollisionScene)
                using (var bodies = scene.Bodies)
                    Check(bodies.Count == 3, "Removed environment body");
                var reloaded = new CollisionDemoModel(body, false);
                Check(reloaded.LinkHulls.Count == 0 && reloaded.Obstacles.Count == 0,
                    "New robot session clears collision edits");
                report.AppendLine("PASS collision scene bindings, obstacle edit, adjacent exemption");
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

        private static uint FindLink(RobotBody body, string name)
        {
            for (uint i = 0; i < body.GetLinkCount(); ++i)
            {
                using var index = new RobotLinkIndex(i);
                using var link = body.GetLink(index);
                if (link.GetName() == name) return i;
            }
            throw new InvalidOperationException("Missing Link: " + name);
        }
        private static CollisionDemoModel.Hull WorldCube(RobotBody body, uint link)
        {
            using var linkIndex = new RobotLinkIndex(link);
            using var world = body.GetLinkTransform(linkIndex, true);
            using var inverse = world.inversed();
            var coordinates = new double[24];
            int vertex = 0;
            foreach (double x in new[] { 1800.0, 2000.0 })
                foreach (double y in new[] { -100.0, 100.0 })
                    foreach (double z in new[] { 1300.0, 1500.0 })
                    {
                        using var point = new Vector3d(x, y, z);
                        point.applyMatrix4d(inverse);
                        coordinates[vertex++] = point.x;
                        coordinates[vertex++] = point.y;
                        coordinates[vertex++] = point.z;
                    }
            uint[] triangles = {
                0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5,
                0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6,
                0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3
            };
            return new CollisionDemoModel.Hull(coordinates, triangles);
        }
        private static int CheckPathSamples(RobotSimulationSession session,
            CollisionDemoModel demo, AnyCAD.Foundation.DoubleList2 path,
            RobotJointPositionList goals, RobotMotionLimitList limits)
        {
            int count = 0;
            for (int segment = 0; segment + 1 < path.Count; ++segment)
            using (var from = path[segment])
            using (var to = path[segment + 1])
            {
                Check(from.Count == goals.Count && to.Count == goals.Count,
                    "Planned path dimension");
                foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
                {
                    using var sampleGoals = new RobotJointPositionList();
                    for (int joint = 0; joint < goals.Count; ++joint)
                    {
                        using var source = goals[joint];
                        using var index = source.JointIndex;
                        using var value = new RobotJointPosition {
                            JointIndex = index,
                            Position = from[joint] + fraction * (to[joint] - from[joint])
                        };
                        sampleGoals.Add(value);
                    }
                    using var request = session.CreatePlanningRequest(sampleGoals, limits);
                    using (var options = request.Options) options.CollisionResolution = 0.05;
                    demo.ApplyTo(request);
                    using var result = session.Plan(request);
                    using var planning = result.Planning;
                    Check(planning.Status != EnumJointSpacePlanningStatus.InvalidGoal,
                        $"Planned segment {segment} has a colliding sample at {fraction}");
                    ++count;
                }
            }
            return count;
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
