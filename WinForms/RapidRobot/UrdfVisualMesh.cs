using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RapidRobot
{
    // Reads the same STL assets referenced by the URDF visual elements. Coordinates are link local.
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
            var points = new List<(double X, double Y, double Z)>();
            var indices = new List<uint>();
            foreach (var visual in link.Elements("visual"))
            {
                var mesh = visual.Element("geometry")?.Element("mesh");
                if (mesh == null) continue;
                var name = (string)mesh.Attribute("filename");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(urdfFile) ?? "", name.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                var scale = Triple((string)mesh.Attribute("scale"), (1, 1, 1));
                var origin = visual.Element("origin");
                var xyz = Triple((string)origin?.Attribute("xyz"), (0, 0, 0));
                var rpy = Triple((string)origin?.Attribute("rpy"), (0, 0, 0));
                foreach (var vertex in ReadStl(file))
                {
                    double x = vertex.X * scale.X * RobotLengthScale;
                    double y = vertex.Y * scale.Y * RobotLengthScale;
                    double z = vertex.Z * scale.Z * RobotLengthScale;
                    var (sr, cr) = Math.SinCos(rpy.X);
                    var (sp, cp) = Math.SinCos(rpy.Y);
                    var (sy, cy) = Math.SinCos(rpy.Z);
                    double y1 = cr * y - sr * z, z1 = sr * y + cr * z;
                    double x2 = cp * x + sp * z1, z2 = -sp * x + cp * z1;
                    points.Add((cy * x2 - sy * y1 + xyz.X * RobotLengthScale,
                        sy * x2 + cy * y1 + xyz.Y * RobotLengthScale,
                        z2 + xyz.Z * RobotLengthScale));
                    indices.Add((uint)indices.Count);
                }
            }
            if (indices.Count == 0)
                throw new InvalidDataException("This Link has no STL visual triangles: " + linkName);
            var result = new CollisionTriangleMesh();
            using (var vertices = result.Vertices)
                foreach (var point in points)
                    using (var value = new Vector3d(point.X, point.Y, point.Z)) vertices.Add(value);
            using (var triangles = result.Indices)
                foreach (var index in indices) triangles.Add(index);
            if (!result.IsValid())
            {
                result.Dispose();
                throw new InvalidDataException("Visual mesh is invalid: " + linkName);
            }
            return result;
        }

        private static (double X, double Y, double Z) Triple(string text,
            (double X, double Y, double Z) fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            var fields = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3) throw new InvalidDataException("Expected three coordinates: " + text);
            return (double.Parse(fields[0], CultureInfo.InvariantCulture),
                double.Parse(fields[1], CultureInfo.InvariantCulture),
                double.Parse(fields[2], CultureInfo.InvariantCulture));
        }

        private static IEnumerable<(double X, double Y, double Z)> ReadStl(string file)
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);
            if (stream.Length >= 84)
            {
                stream.Position = 80;
                uint count = reader.ReadUInt32();
                if ((long)count * 50 + 84 == stream.Length)
                {
                    for (uint i = 0; i < count; ++i)
                    {
                        stream.Position += 12; // face normal
                        for (int j = 0; j < 3; ++j)
                            yield return (reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        stream.Position += 2;
                    }
                    yield break;
                }
            }
            stream.Position = 0;
            using var textReader = new StreamReader(stream, Encoding.UTF8);
            string text = textReader.ReadToEnd();
            var matches = Regex.Matches(text,
                @"\bvertex\s+([-+\d.eE]+)\s+([-+\d.eE]+)\s+([-+\d.eE]+)",
                RegexOptions.IgnoreCase);
            if (matches.Count == 0 || matches.Count % 3 != 0)
                throw new InvalidDataException("STL has no complete triangles: " + file);
            foreach (Match match in matches)
                yield return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        }
    }
}
