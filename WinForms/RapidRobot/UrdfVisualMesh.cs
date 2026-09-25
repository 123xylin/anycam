using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RapidRobot
{
    internal static class UrdfVisualMesh
    {
        // RobotIO.Load uses RobotUrdfLoadOptions.LengthScale = 1000 by default.
        private const double RobotLengthScale = 1000.0;

        internal static CollisionTriangleMesh Load(string urdfFile, string linkName)
        {
            var document = XDocument.Load(urdfFile);
            var link = document.Root?.Elements("link")
                .FirstOrDefault(item => (string)item.Attribute("name") == linkName)
                ?? throw new InvalidDataException("Link is absent from the URDF: " + linkName);
            var parts = new List<StlVisualPart>();
            foreach (var visual in link.Elements("visual"))
            {
                var mesh = visual.Element("geometry")?.Element("mesh");
                if (mesh == null) continue;
                var name = mesh.Attribute("filename")?.Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(urdfFile) ?? "",
                    name.Replace('/', Path.DirectorySeparatorChar)));
                var scale = Triple(mesh.Attribute("scale")?.Value, (1, 1, 1));
                var origin = visual.Element("origin");
                var xyz = Triple(origin?.Attribute("xyz")?.Value, (0, 0, 0));
                var rpy = Triple(origin?.Attribute("rpy")?.Value, (0, 0, 0));
                parts.Add(new StlVisualPart(file,
                    scale.X * RobotLengthScale, scale.Y * RobotLengthScale,
                    scale.Z * RobotLengthScale,
                    xyz.X * RobotLengthScale, xyz.Y * RobotLengthScale,
                    xyz.Z * RobotLengthScale, rpy.X, rpy.Y, rpy.Z));
            }
            return StlLinkMesh.Load(parts);
        }

        private static (double X, double Y, double Z) Triple(string? text,
            (double X, double Y, double Z) fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            var fields = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3) throw new InvalidDataException("Expected three coordinates: " + text);
            return (double.Parse(fields[0], CultureInfo.InvariantCulture),
                double.Parse(fields[1], CultureInfo.InvariantCulture),
                double.Parse(fields[2], CultureInfo.InvariantCulture));
        }
    }
}
