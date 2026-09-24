using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.IO;
using System.Text;

namespace RapidRobot
{
    internal static class ManualKr500Acceptance
    {
        internal static int Run(string reportPath, string modelRoot)
        {
            var report = new StringBuilder();
            try
            {
                var directory = System.IO.Path.Combine(modelRoot, "Kr500");
                using var manual = ManualKr500Factory.Create(directory);
                using var path = new AnyCAD.Foundation.Path(
                    System.IO.Path.Combine(directory, "index.urdf"));
                using var imported = RobotIO.Load(path);
                Check(manual != null && imported != null, "Both Kr500 models must load");
                Check(manual.GetLinkCount() == imported.GetLinkCount() &&
                    manual.GetLinkCount() == 8, "Eight Links");
                Check(manual.GetJointCount() == imported.GetJointCount() &&
                    manual.GetJointCount() == 7, "Seven Joint edges");
                Check(manual.GetArmCount() == imported.GetArmCount() &&
                    manual.GetArmCount() == 1, "One Arm");
                Check(manual.GetFrameCount() == imported.GetFrameCount() &&
                    manual.GetFrameCount() == 1, "One tip Frame");
                using (var model = manual.GetModel())
                using (var root = model.GetRootLinkIndex())
                    Check(root.GetValue() == 0, "Base is the root Link");

                for (uint i = 0; i < manual.GetLinkCount(); ++i)
                {
                    using var index = new RobotLinkIndex(i);
                    using var actual = manual.GetLink(index);
                    using var expected = imported.GetLink(index);
                    Check(actual.GetName() == expected.GetName(), "Link name " + i);
                    using var visuals = actual.GetVisuals();
                    Check(visuals.Count == (i == 7 ? 0 : 1),
                        "Manual STL visual count for " + actual.GetName());
                    if (i == 7) continue;
                    using var a = ManualKr500Factory.LoadLinkMesh(directory, actual.GetName());
                    using var b = UrdfVisualMesh.Load(System.IO.Path.Combine(directory,
                        "index.urdf"), actual.GetName());
                    CompareMeshes(a, b, actual.GetName());
                }
                report.AppendLine("PASS eight Links, seven STL visuals and matching local meshes");

                for (uint i = 0; i < manual.GetJointCount(); ++i)
                {
                    using var index = new RobotJointIndex(i);
                    using var a = manual.GetJoint(index);
                    using var b = imported.GetJoint(index);
                    Check(a.GetName() == b.GetName() && a.GetJointType() == b.GetJointType(),
                        "Joint name/type " + i);
                    using var aParent = a.GetParentLinkIndex();
                    using var bParent = b.GetParentLinkIndex();
                    using var aChild = a.GetChildLinkIndex();
                    using var bChild = b.GetChildLinkIndex();
                    Check(aParent.GetValue() == bParent.GetValue() &&
                        aChild.GetValue() == bChild.GetValue(), "Joint topology " + i);
                    using var aOrigin = a.GetOrigin();
                    using var bOrigin = b.GetOrigin();
                    CompareMatrix(aOrigin, bOrigin, "Joint origin " + i);
                    if (a.GetJointType() == EnumRobotJointType.Fixed) continue;
                    using var aAxis = a.GetAxis();
                    using var bAxis = b.GetAxis();
                    Compare(aAxis.x, bAxis.x, "Joint axis X " + i);
                    Compare(aAxis.y, bAxis.y, "Joint axis Y " + i);
                    Compare(aAxis.z, bAxis.z, "Joint axis Z " + i);
                    Compare(a.GetMinValue(), b.GetMinValue(), "Joint minimum " + i);
                    Compare(a.GetMaxValue(), b.GetMaxValue(), "Joint maximum " + i);
                    Compare(a.GetMaxVelocity(), b.GetMaxVelocity(), "Joint velocity " + i);
                }
                report.AppendLine("PASS Joint names, types, topology, origins, axes and limits");

                using (var index = new RobotFrameIndex(0))
                using (var a = manual.GetFrame(index))
                using (var b = imported.GetFrame(index))
                {
                    Check(a.GetName() == b.GetName(), "Flange Frame name");
                    using var aOrigin = a.GetOrigin();
                    using var bOrigin = b.GetOrigin();
                    CompareMatrix(aOrigin, bOrigin, "Flange Frame origin");
                }
                using (var index = new RobotArmIndex(0))
                using (var a = manual.GetArm(index))
                using (var b = imported.GetArm(index))
                    Check(a.GetName() == b.GetName(), "Arm name");

                CompareFk(manual, imported, "home");
                double[] positions = { 0.2, -0.45, 0.6, -0.3, 0.25, -0.15 };
                for (uint i = 0; i < positions.Length; ++i)
                {
                    using var index = new RobotJointIndex(i);
                    Check(manual.SetJointPosition(index, positions[i]), "Manual joint state " + i);
                    Check(imported.SetJointPosition(index, positions[i]), "URDF joint state " + i);
                }
                manual.UpdateFrames();
                imported.UpdateFrames();
                CompareFk(manual, imported, "nonzero");
                report.AppendLine("PASS home and nonzero Link/Frame FK");

                var collisionReport = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "rapidrobot-manual-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    Check(CollisionDemoAcceptance.RunManual(collisionReport, modelRoot) == 0,
                        File.ReadAllText(collisionReport));
                    report.Append(File.ReadAllText(collisionReport));
                }
                finally { if (File.Exists(collisionReport)) File.Delete(collisionReport); }
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

        private static void CompareMeshes(CollisionTriangleMesh a, CollisionTriangleMesh b,
            string name)
        {
            using var verticesA = a.Vertices;
            using var verticesB = b.Vertices;
            using var trianglesA = a.Indices;
            using var trianglesB = b.Indices;
            Check(verticesA.Count == verticesB.Count && verticesA.Count > 0,
                "STL vertex count " + name);
            Check(trianglesA.Count == trianglesB.Count && trianglesA.Count > 0,
                "STL triangle index count " + name);
            for (int i = 0; i < verticesA.Count; ++i)
            {
                using var x = verticesA[i];
                using var y = verticesB[i];
                Compare(x.x, y.x, name + " vertex X " + i);
                Compare(x.y, y.y, name + " vertex Y " + i);
                Compare(x.z, y.z, name + " vertex Z " + i);
            }
            for (int i = 0; i < trianglesA.Count; ++i)
                Check(trianglesA[i] == trianglesB[i], name + " index " + i);
        }

        private static void CompareFk(RobotBody a, RobotBody b, string state)
        {
            for (uint i = 0; i < a.GetLinkCount(); ++i)
            {
                using var index = new RobotLinkIndex(i);
                using var x = a.GetLinkTransform(index, false);
                using var y = b.GetLinkTransform(index, false);
                CompareMatrix(x, y, state + " Link " + i);
            }
            using var frame = new RobotFrameIndex(0);
            using var tipA = a.GetFrameTransform(frame, false);
            using var tipB = b.GetFrameTransform(frame, false);
            CompareMatrix(tipA, tipB, state + " flange");
        }

        private static void CompareMatrix(Matrix4d a, Matrix4d b, string label)
        {
            foreach (var basis in new[] { (0.0, 0.0, 0.0), (1.0, 0.0, 0.0),
                (0.0, 1.0, 0.0), (0.0, 0.0, 1.0) })
            {
                using var x = new Vector3d(basis.Item1, basis.Item2, basis.Item3);
                using var y = new Vector3d(basis.Item1, basis.Item2, basis.Item3);
                x.applyMatrix4d(a);
                y.applyMatrix4d(b);
                Compare(x.x, y.x, label + " X");
                Compare(x.y, y.y, label + " Y");
                Compare(x.z, y.z, label + " Z");
            }
        }

        private static void Compare(double actual, double expected, string label)
            => Check(Math.Abs(actual - expected) < 1e-5,
                $"{label}: manual={actual}, URDF={expected}");

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
        }
    }
}
