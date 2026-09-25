using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnyCAD.Robot.Studio
{
    // Numeric assembly data is transcribed from Model/3A/index.urdf. Runtime reads
    // only the five BREP files, so a vendor table or assembly UI can replace it.
    public static class Manual3AFactory
    {
        private const double HalfPi = Math.PI / 2;

        private sealed record Visual(string Link, string File, double X, double Y, double Z,
            double Roll, double Pitch, double Yaw);
        private sealed record Joint(string Name, int Parent, int Child, double X, double Y,
            double Z, double Roll, double Pitch, double Yaw,
            EnumRobotJointType Type = EnumRobotJointType.Prismatic);

        private static readonly string[] LinkNames = { "CS", "Y", "X", "A", "T", "T_tip" };
        private static readonly Visual[] Visuals = {
            new("CS", "0.brep", 0, 0, 0, 0, 0, 0),
            new("Y", "1.brep", 0, 0, -299, HalfPi, 0, 0),
            new("X", "2.brep", -299, 0, -457, 0, 0, -HalfPi),
            new("A", "3.brep", -191, -457, -244, -HalfPi, -HalfPi, 0),
            new("T", "4.brep", -32, -244, 457, Math.PI, 0, HalfPi)
        };
        private static readonly Joint[] Joints = {
            new("Y", 0, 1, 0, 299, 0, -HalfPi, 0, 0),
            new("X", 1, 2, 0, -457, 0, HalfPi, -HalfPi, 0),
            new("A", 2, 3, -108, -244, 0, HalfPi, 0, 0),
            new("T", 3, 4, -159, 0, 0, HalfPi, 0, 0),
            new("T_tip_fixed_joint", 4, 5, 0, 0, 0, 0, 0, 0, EnumRobotJointType.Fixed)
        };

        public static CollisionTriangleMesh LoadLinkMesh(string modelDirectory, string linkName)
        {
            Visual spec = Array.Find(Visuals, entry => entry.Link == linkName)
                ?? throw new InvalidDataException("This Link has no BREP visual: " + linkName);
            return BrepLinkMesh.Load(System.IO.Path.Combine(modelDirectory, spec.File),
                spec.X, spec.Y, spec.Z, spec.Roll, spec.Pitch, spec.Yaw);
        }

        public static RobotBody Create(string modelDirectory)
        {
            using var model = new RobotModel();
            model.SetName("XYZ");
            var indices = new List<RobotLinkIndex>();
            try
            {
                foreach (var name in LinkNames)
                {
                    using var link = new RobotLink();
                    link.SetName(name);
                    var spec = Array.Find(Visuals, item => item.Link == name);
                    if (spec != null)
                    {
                        using var shape = BrepLinkMesh.Open(System.IO.Path.Combine(modelDirectory, spec.File));
                        using var node = BrepSceneNode.Create(shape, 0.5, false)
                            ?? throw new InvalidDataException("Cannot display BREP visual: " + spec.File);
                        using var local = RobotAssemblyPose.Create(spec.X, spec.Y, spec.Z,
                            spec.Roll, spec.Pitch, spec.Yaw);
                        node.SetTransform(local);
                        node.SetName(name + ".visual.0");
                        using var color = name == "CS"
                            ? new Vector3(0.0392157f, 0.0392157f, 0)
                            : new Vector3(1, 1, 0);
                        node.SetColor(color);
                        using var visuals = new SceneNodeList();
                        visuals.Add(node);
                        link.SetVisuals(visuals);
                    }
                    indices.Add(model.AddLink(link));
                }
                model.SetRootLink(indices[0]);
                foreach (var spec in Joints)
                {
                    using var joint = new RobotJointEdge();
                    joint.SetName(spec.Name);
                    joint.SetParentLinkIndex(indices[spec.Parent]);
                    joint.SetChildLinkIndex(indices[spec.Child]);
                    joint.SetJointType(spec.Type);
                    using var origin = RobotAssemblyPose.Create(spec.X, spec.Y, spec.Z,
                        spec.Roll, spec.Pitch, spec.Yaw);
                    joint.SetOrigin(origin);
                    if (spec.Type != EnumRobotJointType.Fixed)
                    {
                        using var axis = new Vector3d(0, 0, 1);
                        joint.SetAxis(axis);
                        joint.SetMinValue(-350);
                        joint.SetMaxValue(350);
                        joint.SetMaxVelocity(200);
                        joint.SetHome(0);
                        joint.SetInitialPosition(0);
                    }
                    using var jointIndex = model.AddJoint(joint);
                }
                using var frame = new RobotFrame();
                frame.SetName("T_tip_tip");
                frame.SetLinkIndex(indices[5]);
                using var identity = new Matrix4d(1);
                frame.SetOrigin(identity);
                using var tip = model.AddFrame(frame);
                using var arm = new RobotArm();
                arm.SetName("CS_to_T_tip");
                arm.SetBaseLinkIndex(indices[0]);
                arm.SetTipFrameIndex(tip);
                using var armIndex = model.AddArm(arm);
                return RobotBody.Create(model)
                    ?? throw new InvalidOperationException("The hand-built 3A topology is invalid.");
            }
            finally { foreach (var index in indices) index.Dispose(); }
        }
    }
}
