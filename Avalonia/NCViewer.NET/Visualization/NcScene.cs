using System;
using System.Collections.Generic;
using AnyCAD.Foundation;
using NcViewerNet.Models;

namespace NcViewerNet.Visualization;

public sealed class NcScene
{
    public required IReadOnlyList<NcSegment> Segments { get; init; }
    public required double[] Cumulative { get; init; }
    public required SceneNode ToolNode { get; init; }
    public required double ToolHeight { get; init; }
    public required Vector3d StartPoint { get; init; }
    public required double TotalDistance { get; init; }
}

public static class NcSceneBuilder
{
    private const int MaxDisplaySegments = 200000;

    private static readonly Vector3 RapidColor = ColorTable.Hex(0x2563EB);
    private static readonly Vector3 FeedColor = ColorTable.Hex(0xE11D48);
    private static readonly Vector3 StockColor = ColorTable.Hex(0x64748B);
    private static readonly Vector3 ToolColor = ColorTable.Hex(0x374151);

    public static NcScene Build(IRenderView render, NcDocument document)
    {
        render.ClearAll();

        var segments = document.Segments;
        if (segments.Count == 0)
            throw new InvalidOperationException("No displayable path segments were found.");

        var firstPoint = segments[0].Start;

        AddStockWireframe(render, document);
        AddToolPath(render, segments);
        var tool = AddTool(render, firstPoint);

        render.RequestDraw(EnumUpdateFlags.Scene);
        render.ZoomAll();

        return new NcScene
        {
            Segments = segments,
            Cumulative = document.Cumulative,
            ToolNode = tool.Node,
            ToolHeight = tool.Height,
            StartPoint = firstPoint,
            TotalDistance = document.TotalLength
        };
    }

    private static void AddStockWireframe(IRenderView render, NcDocument document)
    {
        if (!document.HasCutting)
            return;

        var minX = document.MinX;
        var maxX = document.MaxX;
        var minY = document.MinY;
        var maxY = document.MaxY;
        var zBottom = document.MinZ;
        var zTop = 0.0;

        var b000 = new Vector3((float)minX, (float)minY, (float)zBottom);
        var b100 = new Vector3((float)maxX, (float)minY, (float)zBottom);
        var b110 = new Vector3((float)maxX, (float)maxY, (float)zBottom);
        var b010 = new Vector3((float)minX, (float)maxY, (float)zBottom);
        var t000 = new Vector3((float)minX, (float)minY, (float)zTop);
        var t100 = new Vector3((float)maxX, (float)minY, (float)zTop);
        var t110 = new Vector3((float)maxX, (float)maxY, (float)zTop);
        var t010 = new Vector3((float)minX, (float)maxY, (float)zTop);

        var node = new SegmentsSceneNode(12, StockColor, 1);
        var edge = 0u;
        void Edge(Vector3 a, Vector3 b)
        {
            node.SetPositions(edge, a, b);
            node.SetColors(edge, StockColor, StockColor);
            edge++;
        }

        Edge(b000, b100); Edge(b100, b110); Edge(b110, b010); Edge(b010, b000);
        Edge(t000, t100); Edge(t100, t110); Edge(t110, t010); Edge(t010, t000);
        Edge(b000, t000); Edge(b100, t100); Edge(b110, t110); Edge(b010, t010);

        node.UpdateBoundingBox();
        node.RequestUpdate();
        render.ShowSceneNode(node);
    }

    private static void AddToolPath(IRenderView render, IReadOnlyList<NcSegment> segments)
    {
        var count = segments.Count;
        var stride = count > MaxDisplaySegments
            ? (int)Math.Ceiling((double)count / MaxDisplaySegments)
            : 1;
        var displayCount = (count + stride - 1) / stride;

        var node = new SegmentsSceneNode((uint)displayCount, FeedColor, 1);
        var index = 0u;
        for (var i = 0; i < count; i += stride)
        {
            var segment = segments[i];
            var color = segment.Motion == NcMotionType.Rapid ? RapidColor : FeedColor;
            node.SetPositions(index, Vector3.From(segment.Start), Vector3.From(segment.End));
            node.SetColors(index, color, color);
            index++;
        }

        node.UpdateBoundingBox();
        node.RequestUpdate();
        render.ShowSceneNode(node);
    }

    private static ToolInfo AddTool(IRenderView render, Vector3d tip)
    {
        var radius = 6.0f;
        var height = 80.0f;

        var material = MeshStandardMaterial.Create("NcViewer.Tool");
        material.SetColor(ToolColor);
        material.SetMetalness(0.6f);
        material.SetRoughness(0.4f);

        var node = new PrimitiveSceneNode(GeometryBuilder.CreateCylinder(radius, radius, height), material);
        node.SetPickable(false);
        PlaceTool(node, tip, height);
        node.UpdateBoundingBox();
        render.ShowSceneNode(node);
        return new ToolInfo(node, height);
    }

    public static void PlaceTool(SceneNode node, Vector3d tip, double height)
    {
        node.SetTransform(Matrix4d.makeTranslation(tip.x, tip.y, tip.z + height / 2.0));
        node.RequestUpdate();
    }

    private readonly record struct ToolInfo(SceneNode Node, double Height);
}
