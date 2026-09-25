using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnyCAD.Robot.Studio
{
    // Kr500 dimensions and mounting poses transcribed from Model/Kr500/index.urdf.
    // Runtime construction reads only the STL files; no URDF parser is involved.
    public static class ManualKr500Factory
    {
        private const double HalfPi = Math.PI / 2;
        private const double JointLimit = 6.10865238198015;
        private const double JointVelocity = 3.49065850398866;

        private sealed record Visual(string Link, string File, double X, double Y, double Z,
            double Roll = 0, double Pitch = 0, double Yaw = 0);
        private sealed record Joint(string Name, int Parent, int Child, double X, double Y,
            double Z, double Roll = 0, double Pitch = 0, double Yaw = 0,
            EnumRobotJointType Type = EnumRobotJointType.Revolute);

        private static readonly string[] LinkNames = {
            "Base", "AXIS1", "AXIS2", "AXIS3", "AXIS4", "AXIS5", "AXIS6", "Kr500_flange"
        };

        private static readonly Visual[] Visuals = {
            new("Base", "BASE.STL", 0, 0, 0),
            new("AXIS1", "JOINT1.STL", 0, 0, -1045),
            new("AXIS2", "JOINT2.STL", -500, 1045, 0, HalfPi),
            new("AXIS3", "JOINT3.STL", -1045, -1800, 0, HalfPi, 0, HalfPi),
            new("AXIS4", "JOINT4.STL", -1100, 0, 2825, 0, HalfPi),
            new("AXIS5", "JOINT5.STL", -1100, -2825, 0, HalfPi, 0, HalfPi),
            new("AXIS6", "JOINT6.STL", -1100, 0, -3115, Math.PI, -HalfPi)
        };

        private static readonly Joint[] Joints = {
            new("AXIS1", 0, 1, 0, 0, 1045),
            new("AXIS2", 1, 2, 500, 0, 0, -HalfPi),
            new("AXIS3", 2, 3, 1300, 0, 0, 0, 0, -HalfPi),
            new("AXIS4", 3, 4, 55, 1025, 0, HalfPi),
            new("AXIS5", 4, 5, 0, 0, 0, -HalfPi),
            new("AXIS6", 5, 6, 0, 290, 0, -HalfPi),
            new("Kr500_flange_fixed_joint", 6, 7, 0, 0, 0,
                Type: EnumRobotJointType.Fixed)
        };

        public static CollisionTriangleMesh LoadLinkMesh(string modelDirectory, string linkName)
        {
            Visual spec = Array.Find(Visuals, entry => entry.Link == linkName)
                ?? throw new InvalidDataException("This Link has no STL visual: " + linkName);
            var file = System.IO.Path.Combine(modelDirectory, spec.File);
            return StlLinkMesh.Load(new[] { new StlVisualPart(file, 1, 1, 1,
                spec.X, spec.Y, spec.Z, spec.Roll, spec.Pitch, spec.Yaw) });
        }

        public static RobotBody Create(string modelDirectory)
        {
            using var model = new RobotModel();
            model.SetName("Kr500");
            var indices = new List<RobotLinkIndex>();
            try
            {
                foreach (string name in LinkNames)
                {
                    using var link = new RobotLink();
                    link.SetName(name);
                    if (name != "Kr500_flange")
                    {
                        using var mesh = LoadLinkMesh(modelDirectory, name);
                        using var node = CreateVisual(name, mesh);
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
                        joint.SetMinValue(-JointLimit);
                        joint.SetMaxValue(JointLimit);
                        joint.SetMaxVelocity(JointVelocity);
                        joint.SetHome(0);
                        joint.SetInitialPosition(0);
                    }
                    using var jointIndex = model.AddJoint(joint);
                }
                using var frame = new RobotFrame();
                frame.SetName("Kr500_flange_tip");
                frame.SetLinkIndex(indices[7]);
                using var identity = new Matrix4d(1);
                frame.SetOrigin(identity);
                using var tip = model.AddFrame(frame);
                using var arm = new RobotArm();
                arm.SetName("Base_to_Kr500_flange");
                arm.SetBaseLinkIndex(indices[0]);
                arm.SetTipFrameIndex(tip);
                using var armIndex = model.AddArm(arm);
                return RobotBody.Create(model)
                    ?? throw new InvalidOperationException("The hand-built Kr500 topology is invalid.");
            }
            finally { foreach (var index in indices) index.Dispose(); }
        }

        private static PrimitiveSceneNode CreateVisual(string name, CollisionTriangleMesh mesh)
        {
            using var positions = new FloatList();
            using (var vertices = mesh.Vertices)
                foreach (var vertex in vertices)
                    using (vertex)
                    {
                        positions.Add((float)vertex.x);
                        positions.Add((float)vertex.y);
                        positions.Add((float)vertex.z);
                    }
            using var triangles = new Uint32List();
            using (var source = mesh.Indices)
                foreach (var index in source) triangles.Add(index);
            using var positionBuffer = Float32Buffer.Create(positions);
            using var indexBuffer = Uint32Buffer.Create(triangles);
            using var positionArray = new Float32Array(positionBuffer);
            using var indexArray = new Uint32Array(indexBuffer);
            using var geometry = GeometryBuilder.CreateGeometry(EnumPrimitiveType.TRIANGLES,
                positionArray, null, null, indexArray);
            var node = PrimitiveSceneNode.Create(geometry, null);
            node.SetName(name + ".visual.0");
            using var color = name == "Base"
                ? new Vector3(0.0392157f, 0.0392157f, 0)
                : new Vector3(1, 1, 0);
            node.SetColor(color);
            return node;
        }
    }
}
