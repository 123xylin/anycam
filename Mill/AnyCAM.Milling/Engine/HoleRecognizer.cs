using AnyCAD.Foundation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnyCAM.Milling.Engine
{
    public enum HoleKind
    {
        Through,
        Blind,
        Step,
    }

    /// <summary>孔从哪一面进刀（当前 CAM 仅支持顶面 Z+ 向下钻）。</summary>
    public enum HoleAccess
    {
        /// <summary>顶面入刀：通孔或顶面盲孔</summary>
        FromTop,
        /// <summary>底面入刀：自下而上的盲孔，需翻面或单独装夹</summary>
        FromBottom,
    }

    public readonly record struct RecognizedHole(
        double CenterX,
        double CenterY,
        double DiameterMm,
        HoleKind Kind,
        HoleAccess Access,
        double DepthMm,
        double FloorZ,
        int StepClusterId = -1);

    public sealed class HoleDiameterGroup
    {
        public double DiameterMm { get; init; }
        public IReadOnlyList<RecognizedHole> Holes { get; init; } = Array.Empty<RecognizedHole>();
        public string KindSummary { get; init; } = string.Empty;
        /// <summary>台阶孔同轴各段直径，如 大径φ62/小径φ40</summary>
        public string StepDiameterLabel { get; init; } = string.Empty;
        /// <summary>当前装夹（顶面加工）可钻的孔数</summary>
        public int MachinableFromTopCount => Holes.Count(h => h.Access == HoleAccess.FromTop);
        public int BottomAccessCount => Holes.Count(h => h.Access == HoleAccess.FromBottom);

        /// <summary>工序树 / 面板标题用孔径描述</summary>
        public string DrillTitle =>
            string.IsNullOrWhiteSpace(StepDiameterLabel)
                ? $"φ{DiameterMm:F0}"
                : $"{StepDiameterLabel} · 本刀φ{DiameterMm:F0}";
    }

    /// <summary>孔识别自检摘要（CLI / 日志）。</summary>
    public sealed class HoleRecognitionReport
    {
        public int TotalFaces { get; init; }
        public int FacesScanned { get; init; }
        public int CylinderFacesAccepted { get; init; }
        public int RejectedTooLarge { get; init; }
        public int RejectedTooSmall { get; init; }
        public int RejectedNotVertical { get; init; }
        public int RejectedNotCylinder { get; init; }
        public IReadOnlyList<HoleDiameterGroup> Groups { get; init; } = Array.Empty<HoleDiameterGroup>();

        public string ToSummaryText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"面总数={TotalFaces} 扫描={FacesScanned} 圆柱面={CylinderFacesAccepted}");
            sb.AppendLine(
                $"丢弃: 非圆柱={RejectedNotCylinder} 非竖直={RejectedNotVertical} " +
                $"过小={RejectedTooSmall} 过大={RejectedTooLarge}");
            foreach (var g in Groups.OrderBy(x => x.DiameterMm))
            {
                var title = g.DrillTitle;
                sb.AppendLine(
                    $"  {title} · {g.Holes.Count} 段 · 顶面可钻×{g.MachinableFromTopCount} · {g.KindSummary}");
            }

            return sb.ToString().TrimEnd();
        }
    }

    internal static class HoleRecognizer
    {
        private const double AxisToleranceMm = 0.55;
        private const double DiameterBinMm = 0.05;
        private const double MinDiameterMm = 0.8;
        /// <summary>横梁等大孔常见 φ100+；过小会导致树上完全没有对应钻孔 OP。</summary>
        private const double MaxDiameterMm = 260.0;
        /// <summary>小件可扫全部面；大件先筛「像竖直孔壁」的面再解析圆柱。</summary>
        private const int MaxFacesFullScan = 14_000;
        private const int MaxLargePartCylinderCandidates = 28_000;
        private const double VerticalAxisMinZ = 0.72;
        /// <summary>台阶孔内外圈轴心偏差（STEP 常见 0.5～1.5 mm）</summary>
        private const double StepClusterToleranceMm = 2.0;

        public static IReadOnlyList<HoleDiameterGroup> GroupByDiameter(TopoShape? part) =>
            Analyze(part).Groups;

        public static HoleRecognitionReport Analyze(TopoShape? part)
        {
            if (part == null)
                return new HoleRecognitionReport();

            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return new HoleRecognitionReport();

            var partTop = bounds.CornerMax().Z();
            var partBottom = bounds.CornerMin().Z();
            var dz = Math.Max(partTop - partBottom, 1e-3);
            var zTol = Math.Max(dz * 0.02, 0.25);

            var collect = CollectCylindricalSegments(part, out var stats);
            var segments = MergeCylindricalSegments(collect);
            if (segments.Count == 0)
            {
                return new HoleRecognitionReport
                {
                    TotalFaces = stats.TotalFaces,
                    FacesScanned = stats.FacesScanned,
                    CylinderFacesAccepted = stats.CylinderFacesAccepted,
                    RejectedTooLarge = stats.RejectedTooLarge,
                    RejectedTooSmall = stats.RejectedTooSmall,
                    RejectedNotVertical = stats.RejectedNotVertical,
                    RejectedNotCylinder = stats.RejectedNotCylinder,
                    Groups = Array.Empty<HoleDiameterGroup>(),
                };
            }

            var clusterIds = AssignStepClusters(segments, StepClusterToleranceMm);
            var diametersAtCluster = segments
                .Select((s, i) => (clusterIds[i], BinDiameter(s.DiameterMm)))
                .GroupBy(x => x.Item1)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).Distinct().Count());

            var segmentsByCluster = segments
                .Select((s, i) => (clusterIds[i], s))
                .GroupBy(x => x.Item1)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<CylSegment>)g.Select(x => x.s).ToList());

            var holes = new List<RecognizedHole>();
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var clusterId = clusterIds[i];
                segmentsByCluster.TryGetValue(clusterId, out var clusterSegs);
                clusterSegs ??= new[] { seg };

                var kind = ClassifyKind(seg, partTop, partBottom, zTol, clusterId, diametersAtCluster);
                var access = ClassifyAccess(seg, partTop, partBottom, zTol, clusterSegs);
                var floorZ = access == HoleAccess.FromTop
                    ? seg.ZMin
                    : seg.ZMax;
                var depth = access == HoleAccess.FromTop
                    ? Math.Max(partTop - seg.ZMin, 0.5)
                    : Math.Max(seg.ZMax - partBottom, 0.5);

                holes.Add(new RecognizedHole(
                    seg.AxisX,
                    seg.AxisY,
                    seg.DiameterMm,
                    kind,
                    access,
                    depth,
                    floorZ,
                    clusterId));
            }

            PromoteStepInnerTopAccess(holes, segmentsByCluster, partTop, partBottom, zTol);

            var groups = holes
                .GroupBy(h => BinDiameter(h.DiameterMm))
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var list = g.ToList();
                    var stepLabel = BuildStepDiameterLabel(g.Key, list, holes);
                    return new HoleDiameterGroup
                    {
                        DiameterMm = g.Key,
                        Holes = list,
                        StepDiameterLabel = stepLabel,
                        KindSummary = BuildKindSummary(list, stepLabel),
                    };
                })
                .ToList();

            return new HoleRecognitionReport
            {
                TotalFaces = stats.TotalFaces,
                FacesScanned = stats.FacesScanned,
                CylinderFacesAccepted = stats.CylinderFacesAccepted,
                RejectedTooLarge = stats.RejectedTooLarge,
                RejectedTooSmall = stats.RejectedTooSmall,
                RejectedNotVertical = stats.RejectedNotVertical,
                RejectedNotCylinder = stats.RejectedNotCylinder,
                Groups = groups,
            };
        }

        internal static bool IsMachinableFromTop(RecognizedHole hole) =>
            hole.Access == HoleAccess.FromTop;

        private sealed class CollectStats
        {
            public int TotalFaces;
            public int FacesScanned;
            public int CylinderFacesAccepted;
            public int RejectedTooLarge;
            public int RejectedTooSmall;
            public int RejectedNotVertical;
            public int RejectedNotCylinder;
        }

        private sealed class CylSegment
        {
            public double AxisX { get; init; }
            public double AxisY { get; init; }
            public double DiameterMm { get; init; }
            public double ZMin { get; set; }
            public double ZMax { get; set; }
        }

        private static List<CylSegment> CollectCylindricalSegments(TopoShape part, out CollectStats stats)
        {
            stats = new CollectStats();
            var list = new List<CylSegment>();
            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            if (faces == null)
                return list;

            stats.TotalFaces = faces.Count;
            var bounds = part.GetBBox();
            var partTop = bounds.CornerMax().Z();
            var partBottom = bounds.CornerMin().Z();
            var dzPart = Math.Max(partTop - partBottom, 1e-3);
            var zTol = Math.Max(dzPart * 0.02, 0.25);

            foreach (var face in SelectFacesForHoleScan(faces, partTop, partBottom, zTol))
            {
                if (face == null)
                    continue;

                stats.FacesScanned++;

                using var surface = new ParametricSurface(face);
                if (!surface.IsValidGeometry())
                    continue;

                if (surface.GetSurfaceType() != EnumSurfaceType.SurfaceType_Cylinder)
                {
                    stats.RejectedNotCylinder++;
                    continue;
                }

                using var cylinder = surface.TryCylinder();
                if (cylinder == null)
                {
                    stats.RejectedNotCylinder++;
                    continue;
                }

                var radius = cylinder.Radius();
                if (radius <= 0)
                    continue;

                var diameter = radius * 2.0;
                if (diameter < MinDiameterMm)
                {
                    stats.RejectedTooSmall++;
                    continue;
                }

                if (diameter > MaxDiameterMm)
                {
                    stats.RejectedTooLarge++;
                    continue;
                }

                var axis = cylinder.Axis();
                var dir = axis.Direction();
                if (Math.Abs(dir.Z()) < VerticalAxisMinZ)
                {
                    stats.RejectedNotVertical++;
                    continue;
                }

                var loc = axis.Location();
                var box = face.GetBBox();
                if (box.IsVoid())
                    continue;

                stats.CylinderFacesAccepted++;
                list.Add(new CylSegment
                {
                    AxisX = loc.X(),
                    AxisY = loc.Y(),
                    DiameterMm = diameter,
                    ZMin = box.CornerMin().Z(),
                    ZMax = box.CornerMax().Z(),
                });
            }

            return list;
        }

        /// <summary>
        /// 大件不截断「前 N 个面」，而是优先扫顶面附近的竖直壁面，避免大孔圆柱面排在队尾被漏掉。
        /// </summary>
        private static IEnumerable<TopoShape> SelectFacesForHoleScan(
            TopoShapeList faces,
            double partTop,
            double partBottom,
            double zTol)
        {
            var all = new List<TopoShape>(faces.Count);
            foreach (var face in faces)
            {
                if (face != null)
                    all.Add(face);
            }

            if (all.Count <= MaxFacesFullScan)
                return all;

            return all
                .Where(f => IsVerticalWallCandidate(f, zTol))
                .OrderByDescending(f => f.GetBBox().CornerMax().Z())
                .ThenByDescending(f =>
                {
                    var b = f.GetBBox();
                    var dx = Math.Max(b.CornerMax().X() - b.CornerMin().X(), 0);
                    var dy = Math.Max(b.CornerMax().Y() - b.CornerMin().Y(), 0);
                    var dz = Math.Max(b.CornerMax().Z() - b.CornerMin().Z(), 0);
                    return dz * Math.Max(dx, dy);
                })
                .Take(MaxLargePartCylinderCandidates);
        }

        /// <summary>用面包围盒粗筛竖直孔壁，避免对大件每个面做曲面解析。</summary>
        private static bool IsVerticalWallCandidate(TopoShape face, double zTol)
        {
            var box = face.GetBBox();
            if (box.IsVoid())
                return false;

            var dx = Math.Max(box.CornerMax().X() - box.CornerMin().X(), 0);
            var dy = Math.Max(box.CornerMax().Y() - box.CornerMin().Y(), 0);
            var dz = Math.Max(box.CornerMax().Z() - box.CornerMin().Z(), 0);

            if (dz < zTol * 0.5)
                return false;

            var span = Math.Max(dx, dy);
            if (span < MinDiameterMm * 0.25 && dz < 4.0)
                return false;

            if (dz >= span * 0.12)
                return true;

            return dz >= 2.0 && span >= MinDiameterMm * 0.35;
        }

        private static List<CylSegment> MergeCylindricalSegments(List<CylSegment> raw)
        {
            var map = new Dictionary<(int Ax, int Ay, double Diam), CylSegment>();

            foreach (var item in raw)
            {
                var key = (
                    (int)Math.Round(item.AxisX / AxisToleranceMm),
                    (int)Math.Round(item.AxisY / AxisToleranceMm),
                    BinDiameter(item.DiameterMm));

                if (!map.TryGetValue(key, out var seg))
                {
                    map[key] = new CylSegment
                    {
                        AxisX = item.AxisX,
                        AxisY = item.AxisY,
                        DiameterMm = BinDiameter(item.DiameterMm),
                        ZMin = item.ZMin,
                        ZMax = item.ZMax,
                    };
                    continue;
                }

                seg.ZMin = Math.Min(seg.ZMin, item.ZMin);
                seg.ZMax = Math.Max(seg.ZMax, item.ZMax);
            }

            return map.Values.ToList();
        }

        private static int[] AssignStepClusters(IReadOnlyList<CylSegment> segments, double xyToleranceMm)
        {
            var n = segments.Count;
            var parent = new int[n];
            for (var i = 0; i < n; i++)
                parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }

                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b)
                    parent[b] = a;
            }

            var tol2 = xyToleranceMm * xyToleranceMm;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var dx = segments[i].AxisX - segments[j].AxisX;
                    var dy = segments[i].AxisY - segments[j].AxisY;
                    if (dx * dx + dy * dy <= tol2)
                        Union(i, j);
                }
            }

            var ids = new int[n];
            for (var i = 0; i < n; i++)
                ids[i] = Find(i);

            return ids;
        }

        private static void PromoteStepInnerTopAccess(
            List<RecognizedHole> holes,
            IReadOnlyDictionary<int, IReadOnlyList<CylSegment>> segmentsByCluster,
            double partTop,
            double partBottom,
            double zTol)
        {
            foreach (var (clusterId, segs) in segmentsByCluster)
            {
                if (segs.Select(s => BinDiameter(s.DiameterMm)).Distinct().Count() < 2)
                    continue;

                var maxDiam = segs.Max(s => s.DiameterMm);
                var outerCanTop = segs.Any(s =>
                    s.DiameterMm >= maxDiam - 0.25
                    && (ClassifyAccessCore(s, partTop, partBottom, zTol) == HoleAccess.FromTop
                        || s.ZMax >= partTop - TopOpeningTolerance(s, zTol)));

                if (!outerCanTop)
                    continue;

                for (var i = 0; i < holes.Count; i++)
                {
                    var h = holes[i];
                    if (h.StepClusterId != clusterId || h.DiameterMm >= maxDiam - 0.25)
                        continue;

                    var seg = segs.FirstOrDefault(s => Math.Abs(s.DiameterMm - h.DiameterMm) < 0.15);
                    if (seg == null)
                        continue;

                    holes[i] = h with
                    {
                        Access = HoleAccess.FromTop,
                        Kind = HoleKind.Step,
                        FloorZ = seg.ZMin,
                        DepthMm = Math.Max(partTop - seg.ZMin, 0.5),
                    };
                }
            }
        }

        private static HoleKind ClassifyKind(
            CylSegment seg,
            double partTop,
            double partBottom,
            double zTol,
            int clusterId,
            IReadOnlyDictionary<int, int> diametersAtCluster)
        {
            if (diametersAtCluster.TryGetValue(clusterId, out var diamCount) && diamCount > 1)
                return HoleKind.Step;

            var topTol = TopOpeningTolerance(seg, zTol);
            var reachesTop = seg.ZMax >= partTop - topTol;
            var reachesBottom = seg.ZMin <= partBottom + zTol;
            if (reachesTop && reachesBottom)
                return HoleKind.Through;

            return HoleKind.Blind;
        }

        private static HoleAccess ClassifyAccess(
            CylSegment seg,
            double partTop,
            double partBottom,
            double zTol,
            IReadOnlyList<CylSegment> axisSegments)
        {
            var access = ClassifyAccessCore(seg, partTop, partBottom, zTol);
            if (access == HoleAccess.FromTop || axisSegments.Count < 2)
                return access;

            // 台阶孔内圈：上端在沉孔台肩，够不到全局顶面，但外圈从顶面开口 → 仍按顶面入刀
            if (BinDiameter(seg.DiameterMm) >= axisSegments.Max(s => BinDiameter(s.DiameterMm)) - 0.08)
                return access;

            foreach (var outer in axisSegments.OrderByDescending(s => s.DiameterMm))
            {
                if (BinDiameter(outer.DiameterMm) <= BinDiameter(seg.DiameterMm) + 0.08)
                    continue;

                if (ClassifyAccessCore(outer, partTop, partBottom, zTol) == HoleAccess.FromTop
                    || outer.ZMax >= partTop - TopOpeningTolerance(outer, zTol))
                    return HoleAccess.FromTop;
            }

            return access;
        }

        private static HoleAccess ClassifyAccessCore(
            CylSegment seg,
            double partTop,
            double partBottom,
            double zTol)
        {
            var topTol = TopOpeningTolerance(seg, zTol);
            var reachesTop = seg.ZMax >= partTop - topTol;
            var reachesBottom = seg.ZMin <= partBottom + zTol;

            if (reachesTop && reachesBottom)
                return HoleAccess.FromTop;

            if (reachesBottom && !reachesTop)
                return HoleAccess.FromBottom;

            if (reachesTop && !reachesBottom)
                return HoleAccess.FromTop;

            var gapToTop = partTop - seg.ZMax;
            if (gapToTop <= topTol)
                return HoleAccess.FromTop;

            var gapToBottom = seg.ZMin - partBottom;
            return gapToTop <= gapToBottom ? HoleAccess.FromTop : HoleAccess.FromBottom;
        }

        /// <summary>沉头/倒角使孔壁 ZMax 低于全局顶面时，仍视为顶面开口。</summary>
        private static double TopOpeningTolerance(CylSegment seg, double zTol) =>
            Math.Max(zTol, seg.DiameterMm * 0.08 + 1.5);

        private static (int Ax, int Ay) AxisKey(double axisX, double axisY) => (
            (int)Math.Round(axisX / AxisToleranceMm),
            (int)Math.Round(axisY / AxisToleranceMm));

        private static string BuildStepDiameterLabel(
            double groupDiameterMm,
            IReadOnlyList<RecognizedHole> groupHoles,
            IReadOnlyList<RecognizedHole> allHoles)
        {
            if (!groupHoles.Any(h => h.Kind == HoleKind.Step))
                return string.Empty;

            var diameters = new HashSet<double>();
            foreach (var h in groupHoles.Where(x => x.Kind == HoleKind.Step))
            {
                foreach (var other in allHoles)
                {
                    if (h.StepClusterId >= 0 && other.StepClusterId == h.StepClusterId)
                        diameters.Add(BinDiameter(other.DiameterMm));
                    else if (AxisKey(other.CenterX, other.CenterY) == AxisKey(h.CenterX, h.CenterY))
                        diameters.Add(BinDiameter(other.DiameterMm));
                }
            }

            if (diameters.Count < 2)
                return string.Empty;

            var sorted = diameters.OrderBy(d => d).ToList();
            if (sorted.Count == 2)
                return $"大径φ{sorted[1]:F0}/小径φ{sorted[0]:F0}";

            return string.Join("/", sorted.Select(d => $"φ{d:F0}"));
        }

        private static string BuildKindSummary(IReadOnlyList<RecognizedHole> holes, string stepDiameterLabel)
        {
            if (holes.Count == 0)
                return string.Empty;

            var parts = new List<string>();
            var through = holes.Count(h => h.Kind == HoleKind.Through);
            var topBlind = holes.Count(h => h.Kind == HoleKind.Blind && h.Access == HoleAccess.FromTop);
            var bottomBlind = holes.Count(h => h.Kind == HoleKind.Blind && h.Access == HoleAccess.FromBottom);
            var step = holes.Count(h => h.Kind == HoleKind.Step);

            if (through > 0)
                parts.Add($"通孔×{through}");
            if (topBlind > 0)
                parts.Add($"顶面盲孔×{topBlind}");
            if (bottomBlind > 0)
                parts.Add($"底面盲孔×{bottomBlind}（需翻面）");
            if (step > 0)
            {
                parts.Add(string.IsNullOrWhiteSpace(stepDiameterLabel)
                    ? $"台阶孔×{step}"
                    : $"台阶孔×{step} · {stepDiameterLabel}");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }

        internal static string FormatKindLabel(HoleKind kind) => kind switch
        {
            HoleKind.Through => "通孔",
            HoleKind.Blind => "盲孔",
            HoleKind.Step => "台阶孔",
            _ => "孔",
        };

        private static double BinDiameter(double diameter) =>
            Math.Round(diameter / DiameterBinMm) * DiameterBinMm;
    }
}
