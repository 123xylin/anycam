using System;
using System.Collections.Generic;
using System.Linq;
using AnyCAD.Foundation;
using AnyTubeNet.Models;

namespace AnyTubeNet.Visualization;

public sealed class AnyTubeScene
{
    public required IReadOnlyList<TubePathSegment> PathSegments { get; init; }
    public required IReadOnlyList<TubePathGroup> PathGroups { get; init; }
    public required IReadOnlyList<SceneNode> MovingNodes { get; init; }
    public required SceneNode LaserHeadNode { get; init; }
    public required double LaserHeadHeight { get; init; }
    public required double TotalPathLength { get; init; }
    public required Vector3d FirstPoint { get; init; }
}

public static class AnyTubeSceneBuilder
{
    private static readonly Vector3 CutColor = ColorTable.Hex(0xE11D48);
    private static readonly Vector3 RapidColor = ColorTable.Hex(0x2563EB);
    private static readonly Vector3 LeadColor = ColorTable.Hex(0xF59E0B);
    private static readonly Vector3 TubeColor = ColorTable.Hex(0xCBD5E1);
    private static readonly Vector3 TubeEdgeColor = ColorTable.Hex(0x64748B);
    private static readonly Vector3 PointColor = ColorTable.Hex(0x16A34A);
    private static readonly Vector3 LaserHeadColor = ColorTable.Hex(0x374151);

    public static AnyTubeScene Build(IRenderView render, AnyTubeDocument document)
    {
        render.ClearAll();

        var pathGroups = BuildPathGroups(document);
        var pathSegments = pathGroups.SelectMany(g => g.Segments).ToList();
        if (pathSegments.Count == 0)
            throw new InvalidOperationException("No displayable path segments were found.");

        var movingNodes = new List<SceneNode>();
        var firstPoint = pathSegments[0].Start;

        var pipeNode = AddPipe(render, document);
        if (pipeNode is not null)
            movingNodes.Add(pipeNode);

        movingNodes.Add(AddPath(render, pathSegments));
        movingNodes.Add(AddSamplePoints(render, pathSegments));
        var laserHead = AddLaserHead(render, firstPoint.z);

        render.RequestDraw(EnumUpdateFlags.Scene);
        render.ZoomAll();

        return new AnyTubeScene
        {
            PathSegments = pathSegments,
            PathGroups = pathGroups,
            MovingNodes = movingNodes,
            LaserHeadNode = laserHead.Node,
            LaserHeadHeight = laserHead.Height,
            TotalPathLength = pathSegments.Sum(s => s.Length),
            FirstPoint = firstPoint
        };
    }

    private static List<TubePathGroup> BuildPathGroups(AnyTubeDocument document)
    {
        return document.Contours
            .Select(c => new TubePathGroup(
                c.Type,
                c.Segments
                    .Where(s => s.SegmentLength > 0)
                    .Select(ToPathSegment)
                    .ToList()))
            .Where(g => g.Segments.Count > 0)
            .ToList();
    }

    private static TubePathSegment ToPathSegment(AnyTubeSegment segment)
    {
        return new TubePathSegment(
            segment.Start,
            segment.End,
            DirectionOrFallback(segment.SGV?.ToVector3d(), segment.End - segment.Start),
            DirectionOrFallback(segment.EGV?.ToVector3d(), segment.End - segment.Start),
            segment.Stage,
            segment.Type);
    }

    private static Vector3d DirectionOrFallback(Vector3d? direction, Vector3d fallback)
    {
        if (IsUsableDirection(direction))
            return direction!.normalized();

        if (IsUsableDirection(fallback))
            return fallback.normalized();

        return new Vector3d(0, 0, -1);
    }

    private static bool IsUsableDirection(Vector3d? direction)
    {
        if (direction is null)
            return false;

        var length = direction.length();
        return !double.IsNaN(length) && !double.IsInfinity(length) && length > 1e-9;
    }

    private static SceneNode? AddPipe(IRenderView render, AnyTubeDocument document)
    {
        if (document.Pipe is null || document.Pipe.Length <= 0)
            return null;

        var face = BuildPipeSectionFace(document);
        if (face is null)
            return null;

        var solid = FeatureTool.Extrude(face, document.Pipe.Length, new GDir(0, 1, 0));
        var faceMaterial = MeshStandardMaterial.Create("AnyTube.Pipe");
        faceMaterial.SetColor(TubeColor);
        faceMaterial.SetOpacity(0.28f);
        faceMaterial.SetTransparent(true);
        faceMaterial.SetFaceSide(EnumFaceSide.DoubleSide);

        var edgeMaterial = BasicMaterial.Create("AnyTube.PipeEdges");
        edgeMaterial.SetColor(TubeEdgeColor);
        edgeMaterial.SetLineWidth(1);

        var node = BrepSceneNode.Create(solid, faceMaterial, edgeMaterial, 0.1, true);
        node.SetPickable(false);
        render.ShowSceneNode(node);
        return node;
    }

    private static TopoShape? BuildPipeSectionFace(AnyTubeDocument document)
    {
        var outer = BuildContourWire(document.PipeContour?.OuterContour);
        if (outer is null)
            return null;

        var inner = BuildContourWire(document.PipeContour?.InnerContour);
        if (inner is null)
        {
            var outerFace = CurveBuilder.MakePlanarFace(outer);
            return outerFace;
        }

        var comp = ShapeBuilder.MakeCompound(new TopoShapeList { outer, inner });
        if (comp is null)
            return null;

        var face = CurveBuilder.MakePlanarFace(comp);
        return face;
    }

    private static TopoShape? BuildContourWire(IReadOnlyList<AnyTubeContourEdge>? contour)
    {
        if (contour is null || contour.Count == 0)
            return null;

        var edges = new TopoShapeList();
        foreach (var edge in contour)
        {
            foreach (var shape in BuildContourEdge(edge))
                edges.Add(shape);
        }

        return edges.Count == 0 ? null : CurveBuilder.MakeWire(edges);
    }

    private static IEnumerable<TopoShape> BuildContourEdge(AnyTubeContourEdge edge)
    {
        var arc = BuildContourArc(edge);
        if (arc is not null)
        {
            yield return arc;
            yield break;
        }

        var samples = SampleEdge(edge);
        for (var i = 1; i < samples.Count; i++)
            yield return CurveBuilder.MakeLine(ToProfilePoint(samples[i - 1]), ToProfilePoint(samples[i]));
    }

    private static TopoShape? BuildContourArc(AnyTubeContourEdge edge)
    {
        if (edge.Type != 1 || edge.Center is null || edge.Angle <= 0)
            return null;

        try
        {
            var middle = ArcPointAtRatio(edge, 0.5);
            return CurveBuilder.MakeArcBy3Points(ToProfilePoint(edge.SP), ToProfilePoint(edge.EP), ToProfilePoint(middle));
        }
        catch
        {
            return null;
        }
    }

    private static GPnt ToProfilePoint(Point2 point)
    {
        return new GPnt(point.X, 0, point.Z);
    }

    private static List<Point2> SampleEdge(AnyTubeContourEdge edge)
    {
        if (edge.Type != 1 || edge.Center is null || edge.Angle <= 0)
            return [edge.SP, edge.EP];

        var startAngle = Math.Atan2(edge.SP.Z - edge.Center.Z, edge.SP.X - edge.Center.X);
        var sweepAngle = ResolveArcSweepAngle(edge);
        var radius = Math.Sqrt(Math.Pow(edge.SP.X - edge.Center.X, 2) + Math.Pow(edge.SP.Z - edge.Center.Z, 2));
        var steps = Math.Max(4, (int)Math.Ceiling(edge.Angle / (Math.PI / 16.0)));
        var result = new List<Point2>(steps + 1);
        for (var i = 0; i <= steps; i++)
            result.Add(ArcPointAtRatio(edge, (double)i / steps, startAngle, sweepAngle, radius));

        return result;
    }

    private static Point2 ArcPointAtRatio(AnyTubeContourEdge edge, double ratio)
    {
        if (edge.Center is null)
            return edge.SP;

        var startAngle = Math.Atan2(edge.SP.Z - edge.Center.Z, edge.SP.X - edge.Center.X);
        var sweepAngle = ResolveArcSweepAngle(edge);
        var radius = Math.Sqrt(Math.Pow(edge.SP.X - edge.Center.X, 2) + Math.Pow(edge.SP.Z - edge.Center.Z, 2));
        return ArcPointAtRatio(edge, ratio, startAngle, sweepAngle, radius);
    }

    private static Point2 ArcPointAtRatio(AnyTubeContourEdge edge, double ratio, double startAngle, double sweepAngle, double radius)
    {
        if (edge.Center is null)
            return edge.SP;

        var angle = startAngle + sweepAngle * Math.Clamp(ratio, 0.0, 1.0);
        return new Point2
        {
            X = edge.Center.X + Math.Cos(angle) * radius,
            Z = edge.Center.Z + Math.Sin(angle) * radius
        };
    }

    private static double ResolveArcSweepAngle(AnyTubeContourEdge edge)
    {
        if (edge.Center is null || edge.Angle <= 0)
            return 0;

        var startAngle = Math.Atan2(edge.SP.Z - edge.Center.Z, edge.SP.X - edge.Center.X);
        var endAngle = Math.Atan2(edge.EP.Z - edge.Center.Z, edge.EP.X - edge.Center.X);
        var ccwSweep = NormalizePositiveAngle(endAngle - startAngle);
        var cwSweep = ccwSweep - Math.PI * 2.0;

        if (Math.Abs(ccwSweep - edge.Angle) <= Math.Abs(Math.Abs(cwSweep) - edge.Angle))
            return ccwSweep;

        return cwSweep;
    }

    private static double NormalizePositiveAngle(double angle)
    {
        while (angle < 0)
            angle += Math.PI * 2.0;

        while (angle >= Math.PI * 2.0)
            angle -= Math.PI * 2.0;

        return angle;
    }

    private static SegmentsSceneNode AddPath(IRenderView render, IReadOnlyList<TubePathSegment> segments)
    {
        var node = new SegmentsSceneNode((uint)segments.Count, CutColor, 2);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var color = SegmentColor(segment);
            node.SetPositions((uint)i, Vector3.From(segment.Start), Vector3.From(segment.End));
            node.SetColors((uint)i, color, color);
        }
        node.UpdateBoundingBox();
        node.RequestUpdate();
        render.ShowSceneNode(node);
        return node;
    }

    private static ParticleSceneNode AddSamplePoints(IRenderView render, IReadOnlyList<TubePathSegment> segments)
    {
        var maxPointCount = Math.Min(segments.Count + 1, 1500);
        var stride = Math.Max(1, segments.Count / Math.Max(1, maxPointCount - 1));
        var points = new ParticleSceneNode((uint)maxPointCount, PointColor, 3.0f);

        var index = 0u;
        for (var i = 0; i < segments.Count && index < maxPointCount; i += stride)
            points.SetPosition(index++, Vector3.From(segments[i].Start));

        while (index < maxPointCount)
            points.SetPosition(index++, Vector3.From(segments[^1].End));

        points.UpdateBoundingBox();
        points.RequestUpdate();
        render.ShowSceneNode(points);
        return points;
    }

    private static LaserHeadInfo AddLaserHead(IRenderView render, double tipZ)
    {
        var headHeight = 30.0f;

        var material = MeshStandardMaterial.Create("AnyTube.LaserHead");
        material.SetColor(LaserHeadColor);
        material.SetMetalness(0.25f);
        material.SetRoughness(0.55f);

        var node = new PrimitiveSceneNode(GeometryBuilder.CreateCylinder(3.5f, 2.5f, headHeight), material);
        var translation = Matrix4d.makeTranslation(0, 0, tipZ + headHeight / 2.0);
        node.SetTransform(translation);
        node.SetPickable(false);
        node.UpdateBoundingBox();
        render.ShowSceneNode(node);
        return new LaserHeadInfo(node, headHeight);
    }

    private static Vector3 SegmentColor(TubePathSegment segment)
    {
        if (segment.IsRapid)
            return RapidColor;
        return segment.IsCut ? CutColor : LeadColor;
    }

    private readonly record struct LaserHeadInfo(SceneNode Node, double Height);
}
