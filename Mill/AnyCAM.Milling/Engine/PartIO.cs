using AnyCAD.Exchange;
using AnyCAD.Foundation;
using System;
using System.IO;
using IOPath = System.IO.Path;

namespace AnyCAM.Milling.Engine
{
    /// <summary>从磁盘加载零件几何，统一为适合 CAM/显示的单个 TopoShape。</summary>
    public static class PartIO
    {
        public static TopoShape? Open(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
                return null;

            var shape = ShapeIO.Open(fileName);
            if (shape == null)
            {
                var ext = IOPath.GetExtension(fileName);
                if (IsStep(ext))
                    shape = OpenStepWithCadReader(fileName);
            }

            return shape == null ? null : Normalize(shape);
        }

        private static bool IsStep(string ext)
        {
            ext = ext.ToLowerInvariant();
            return ext is ".stp" or ".step";
        }

        /// <summary>ShapeIO 失败时，用 XDE 读 STEP（装配/带颜色）。</summary>
        private static TopoShape? OpenStepWithCadReader(string fileName)
        {
            var pieces = new TopoShapeList();
            var reader = new CADReader();
            reader.Open(fileName, (_, shape, trf, _) =>
            {
                if (shape == null)
                    return;

                var piece = TransformTool.Transform(shape, trf);
                AppendSolids(pieces, piece);
            });

            if (pieces.Count == 0)
                return null;

            return MergeSolids(pieces);
        }

        private static void AppendSolids(TopoShapeList target, TopoShape shape)
        {
            var solids = shape.GetChildren(EnumTopoShapeType.Topo_SOLID);
            if (solids.Count > 0)
            {
                for (var i = 0; i < solids.Count; i++)
                    target.Add(solids[i]);
                return;
            }

            target.Add(shape);
        }

        private static TopoShape Normalize(TopoShape shape)
        {
            var solids = shape.GetChildren(EnumTopoShapeType.Topo_SOLID);
            if (solids.Count == 0)
                return shape;

            return MergeSolids(solids);
        }

        private static TopoShape MergeSolids(TopoShapeList solids)
        {
            if (solids.Count == 1)
                return solids[0];

            var merged = solids[0];
            for (var i = 1; i < solids.Count; i++)
            {
                var fused = BooleanTool.Boolean(merged, solids[i], EnumBooleanType.Fuse);
                if (fused != null)
                    merged = fused;
            }

            return BooleanTool.Unify(merged, true, true, true) ?? merged;
        }
    }
}
