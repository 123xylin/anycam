using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.IO;

namespace AnyCAD.Robot.Studio
{
    public static class BrepLinkMesh
    {
        public static TopoShape Open(string file)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("BREP visual is missing", file);
            var shape = ShapeIO.Open(file);
            if (shape == null || shape.IsNullShape())
            {
                shape?.Dispose();
                throw new InvalidDataException("Cannot read BREP visual: " + file);
            }
            return shape;
        }

        // ShapeIO's registered STL writer tessellates the same TopoShape shown by
        // BrepSceneNode. Reuse the STL transform path to preserve Link-local units.
        public static CollisionTriangleMesh Load(string file,
            double x, double y, double z, double roll, double pitch, double yaw)
        {
            using var shape = Open(file);
            var stl = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "anyrobot-brep-" + Guid.NewGuid().ToString("N") + ".stl");
            try
            {
                if (!ShapeIO.Save(shape, stl))
                    throw new InvalidDataException("Cannot tessellate BREP visual: " + file);
                return StlLinkMesh.Load(new[] {
                    new StlVisualPart(stl, 1, 1, 1, x, y, z, roll, pitch, yaw)
                });
            }
            finally { if (File.Exists(stl)) File.Delete(stl); }
        }
    }
}
