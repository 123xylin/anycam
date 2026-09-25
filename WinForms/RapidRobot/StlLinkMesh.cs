using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RapidRobot
{
    // Coordinates and translations are millimetres; RPY angles are radians.
    internal readonly record struct StlVisualPart(string File, double ScaleX, double ScaleY,
        double ScaleZ, double X, double Y, double Z, double Roll, double Pitch, double Yaw);

    internal static class StlLinkMesh
    {
        internal static CollisionTriangleMesh Load(IEnumerable<StlVisualPart> parts)
        {
            var points = new List<(double X, double Y, double Z)>();
            var indices = new List<uint>();
            foreach (var part in parts)
            {
                var (sr, cr) = Math.SinCos(part.Roll);
                var (sp, cp) = Math.SinCos(part.Pitch);
                var (sy, cy) = Math.SinCos(part.Yaw);
                foreach (var vertex in ReadStl(part.File))
                {
                    double x = vertex.X * part.ScaleX;
                    double y = vertex.Y * part.ScaleY;
                    double z = vertex.Z * part.ScaleZ;
                    double y1 = cr * y - sr * z, z1 = sr * y + cr * z;
                    double x2 = cp * x + sp * z1, z2 = -sp * x + cp * z1;
                    points.Add((cy * x2 - sy * y1 + part.X,
                        sy * x2 + cy * y1 + part.Y, z2 + part.Z));
                    indices.Add((uint)indices.Count);
                }
            }
            if (indices.Count == 0)
                throw new InvalidDataException("This Link has no STL visual triangles.");
            var result = new CollisionTriangleMesh();
            using (var vertices = result.Vertices)
                foreach (var point in points)
                    using (var value = new Vector3d(point.X, point.Y, point.Z)) vertices.Add(value);
            using (var triangles = result.Indices)
                foreach (var index in indices) triangles.Add(index);
            if (!result.IsValid())
            {
                result.Dispose();
                throw new InvalidDataException("The STL visual mesh is invalid.");
            }
            return result;
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
                        stream.Position += 12;
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
