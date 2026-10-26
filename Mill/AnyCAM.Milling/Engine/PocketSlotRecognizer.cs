using AnyCAD.Foundation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AnyCAM.Milling.Engine
{
    /// <summary>型腔从哪一面进刀（当前 2D 挖槽仅支持顶面 Z+ 向下）。</summary>
    public enum PocketAccess
    {
        FromTop,
        /// <summary>底面/背面开口的腔，需翻面</summary>
        FromBottom,
    }

    /// <summary>识别到的单个型腔槽底。</summary>
    public sealed class PocketSlotInfo
    {
        public TopoShape BottomFace { get; init; } = null!;
        public double LengthMm { get; init; }
        public double WidthMm { get; init; }
        public double DepthMm { get; init; }
        public double AreaMm2 { get; init; }
        public double FloorZ { get; init; }
        public PocketAccess Access { get; init; }
    }

    /// <summary>深度与面积相同的一类槽，对应一条 2D 挖槽工序。</summary>
    public sealed class PocketSlotGroup
    {
        public double LengthMm { get; init; }
        public double WidthMm { get; init; }
        public double DepthMm { get; init; }
        public double AreaMm2 { get; init; }
        public IReadOnlyList<TopoShape> BottomFaces { get; init; } = Array.Empty<TopoShape>();
        public int Count => BottomFaces.Count;
    }

    internal static class PocketSlotRecognizer
    {
        private const double MinPocketArea = 180.0;
        private const double MinPocketSpan = 12.0;
        private const double MinPocketWidth = 3.0;
        private const double DepthBinMm = 0.5;
        private const double AreaBinMm2 = 25.0;

        public static IReadOnlyList<PocketSlotInfo> Recognize(TopoShape? part) =>
            Recognize(part, includeBottomAccess: true);

        public static IReadOnlyList<PocketSlotInfo> RecognizeMachinableFromTop(TopoShape? part) =>
            Recognize(part, includeBottomAccess: false);

        internal static bool IsMachinableFromTop(PocketSlotInfo slot) =>
            slot.Access == PocketAccess.FromTop;

        private static IReadOnlyList<PocketSlotInfo> Recognize(TopoShape? part, bool includeBottomAccess)
        {
            if (part == null)
                return Array.Empty<PocketSlotInfo>();

            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return Array.Empty<PocketSlotInfo>();

            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            if (faces == null || faces.Count == 0)
                return Array.Empty<PocketSlotInfo>();

            var bottomZ = bounds.CornerMin().Z();
            var dz = bounds.CornerMax().Z() - bottomZ;
            var tolerance = Math.Max(dz * 0.01, 1e-3);
            var zTol = Math.Max(dz * 0.02, 0.25);
            var topZ = bounds.CornerMax().Z();
            var topFootprint = HorizontalArea(bounds.CornerMax(), bounds.CornerMin());

            var levels = new List<(double Z, TopoShape Face, double Area, double Len, double Wid)>();

            foreach (var face in faces)
            {
                if (face == null)
                    continue;

                var faceBounds = face.GetBBox();
                if (faceBounds.IsVoid())
                    continue;

                if (faceBounds.CornerMax().Z() - faceBounds.CornerMin().Z() > tolerance)
                    continue;

                var z = faceBounds.CornerMax().Z();
                if (z >= topZ - tolerance)
                    continue;

                var w = faceBounds.CornerMax().X() - faceBounds.CornerMin().X();
                var h = faceBounds.CornerMax().Y() - faceBounds.CornerMin().Y();
                var len = Math.Max(w, h);
                var wid = Math.Min(w, h);
                var area = Math.Max(w, 0) * Math.Max(h, 0);

                if (wid < MinPocketWidth)
                    continue;

                if (len < MinPocketSpan && area < MinPocketArea * 2)
                    continue;

                if (area < MinPocketArea)
                    continue;

                if (z <= bottomZ + tolerance
                    && area > topFootprint * 0.85)
                    continue;

                if (area >= topFootprint * 0.98)
                    continue;

                levels.Add((z, face, area, len, wid));
            }

            if (levels.Count == 0)
                return Array.Empty<PocketSlotInfo>();

            var slots = new List<PocketSlotInfo>();
            foreach (var zLevel in levels
                         .GroupBy(x => Math.Round(x.Z / tolerance) * tolerance)
                         .OrderByDescending(g => g.Key))
            {
                foreach (var item in zLevel.OrderByDescending(x => x.Area))
                {
                    var access = ClassifyAccess(item.Z, topZ, bottomZ, zTol);
                    if (!includeBottomAccess && access == PocketAccess.FromBottom)
                        continue;

                    slots.Add(new PocketSlotInfo
                    {
                        BottomFace = item.Face,
                        LengthMm = item.Len,
                        WidthMm = item.Wid,
                        DepthMm = Math.Max(topZ - item.Z, 0),
                        AreaMm2 = item.Area,
                        FloorZ = item.Z,
                        Access = access,
                    });
                }
            }

            return slots;
        }

        /// <summary>
        /// 槽底更靠近零件底且明显低于顶面 → 视为底面/背面腔（与底面孔同理）。
        /// </summary>
        private static PocketAccess ClassifyAccess(double floorZ, double topZ, double bottomZ, double zTol)
        {
            var depthFromTop = topZ - floorZ;
            var depthFromBottom = floorZ - bottomZ;

            if (depthFromBottom <= zTol)
                return PocketAccess.FromBottom;

            if (depthFromTop <= zTol)
                return PocketAccess.FromTop;

            if (depthFromBottom < depthFromTop - zTol)
                return PocketAccess.FromBottom;

            return PocketAccess.FromTop;
        }

        public static int CountBottomAccess(IReadOnlyList<PocketSlotInfo> slots) =>
            slots.Count(s => s.Access == PocketAccess.FromBottom);

        public static IReadOnlyList<PocketSlotGroup> GroupByDepthAndArea(IReadOnlyList<PocketSlotInfo> slots)
        {
            var machinable = slots.Where(IsMachinableFromTop).ToList();
            if (machinable.Count == 0)
                return Array.Empty<PocketSlotGroup>();

            return machinable
                .GroupBy(s => (
                    Depth: Math.Round(s.DepthMm / DepthBinMm) * DepthBinMm,
                    Area: Math.Round(s.AreaMm2 / AreaBinMm2) * AreaBinMm2))
                .OrderByDescending(g => g.Key.Depth)
                .ThenByDescending(g => g.Key.Area)
                .Select(g =>
                {
                    var first = g.OrderByDescending(x => x.AreaMm2).First();
                    return new PocketSlotGroup
                    {
                        LengthMm = first.LengthMm,
                        WidthMm = first.WidthMm,
                        DepthMm = g.Key.Depth,
                        AreaMm2 = g.Key.Area,
                        BottomFaces = g.Select(x => x.BottomFace).ToList(),
                    };
                })
                .ToList();
        }

        internal static string FormatGroupLabel(PocketSlotGroup group)
        {
            var size = $"{group.LengthMm:F0}×{group.WidthMm:F0}×{group.DepthMm:F0}";
            return group.Count > 1
                ? $"{size} mm · ×{group.Count}"
                : $"{size} mm";
        }

        internal static string FormatGroupGeometry(PocketSlotGroup group)
        {
            var head = $"L{group.LengthMm:F0} × W{group.WidthMm:F0} × 深{group.DepthMm:F0} mm · 面积 {group.AreaMm2:F0} mm²";
            if (group.Count <= 1)
                return head;

            return $"{head} · 同规格 {group.Count} 处";
        }

        internal static string FormatGroupGeometry(PocketSlotGroup group, int skippedBottomAccess)
        {
            var head = FormatGroupGeometry(group);
            if (skippedBottomAccess <= 0)
                return head;

            return $"{head} · 已跳过背面/底面槽×{skippedBottomAccess}";
        }

        private static double HorizontalArea(GPnt cornerMax, GPnt cornerMin)
        {
            var dx = cornerMax.X() - cornerMin.X();
            var dy = cornerMax.Y() - cornerMin.Y();
            return Math.Max(dx, 0) * Math.Max(dy, 0);
        }
    }
}
