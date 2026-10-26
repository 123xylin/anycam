using AnyCAD.Foundation;

namespace AnyCAM.Milling.Engine
{
    /// <summary>把用户指定的 B-rep 面旋转为加工顶面（法向对齐 +Z，再平移到原点）。</summary>
    internal static class PartTopOrientation
    {
        private const double Epsilon = 1e-9;

        public static bool TryGetOutwardNormal(TopoShape face, TopoShape solid, out GDir normal)
        {
            normal = new GDir(0, 0, 1);
            if (face == null || solid == null)
                return false;

            using var surface = new ParametricSurface(face);
            if (!surface.IsValidGeometry())
                return false;

            var faceBounds = face.GetBBox();
            if (faceBounds.IsVoid())
                return false;

            var center = new GPnt(
                (faceBounds.CornerMin().X() + faceBounds.CornerMax().X()) * 0.5,
                (faceBounds.CornerMin().Y() + faceBounds.CornerMax().Y()) * 0.5,
                (faceBounds.CornerMin().Z() + faceBounds.CornerMax().Z()) * 0.5);

            var uv = surface.GetUV(center);
            var n = surface.GetNormal(uv.X(), uv.Y());
            if (n == null)
                return false;

            var nx = n.X();
            var ny = n.Y();
            var nz = n.Z();
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < Epsilon)
                return false;

            nx /= len;
            ny /= len;
            nz /= len;

            var solidBounds = solid.GetBBox();
            if (!solidBounds.IsVoid())
            {
                var cx = (solidBounds.CornerMin().X() + solidBounds.CornerMax().X()) * 0.5;
                var cy = (solidBounds.CornerMin().Y() + solidBounds.CornerMax().Y()) * 0.5;
                var cz = (solidBounds.CornerMin().Z() + solidBounds.CornerMax().Z()) * 0.5;
                var vx = center.X() - cx;
                var vy = center.Y() - cy;
                var vz = center.Z() - cz;
                if (vx * nx + vy * ny + vz * nz < 0)
                {
                    nx = -nx;
                    ny = -ny;
                    nz = -nz;
                }
            }

            normal = new GDir(nx, ny, nz);
            return true;
        }

        public static TopoShape AlignSolidTopNormalToZ(TopoShape part, GDir topNormal)
        {
            var target = new GVec(0, 0, 1);
            var source = new GVec(topNormal.X(), topNormal.Y(), topNormal.Z());

            var q = new GQuaternion();
            q.SetRotation(source, target);

            var tr = new GTrsf();
            tr.SetRotation(q);
            return TransformTool.Transform(part, tr);
        }

        public static string DescribeTopFace(TopoShape part)
        {
            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return "顶面（未识别）";

            var dx = bounds.CornerMax().X() - bounds.CornerMin().X();
            var dy = bounds.CornerMax().Y() - bounds.CornerMin().Y();
            var topZ = bounds.CornerMax().Z();
            return $"顶面 {dx:F0}×{dy:F0} @ Z{topZ:F0}";
        }
    }
}
