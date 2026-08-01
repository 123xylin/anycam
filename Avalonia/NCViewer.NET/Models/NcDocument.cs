using System.Collections.Generic;
using AnyCAD.Foundation;

namespace NcViewerNet.Models;

public enum NcMotionType
{
    Rapid,
    Feed
}

public readonly struct NcSegment
{
    public Vector3d Start { get; }
    public Vector3d End { get; }
    public NcMotionType Motion { get; }
    public double Feed { get; }
    public double Length { get; }

    public NcSegment(Vector3d start, Vector3d end, NcMotionType motion, double feed)
    {
        Start = start;
        End = end;
        Motion = motion;
        Feed = feed;
        Length = (end - start).length();
    }
}

public sealed class NcDocument
{
    public int ToolNumber { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public double ToolDiameter { get; set; }

    public List<NcSegment> Segments { get; } = new();

    public double[] Cumulative { get; set; } = [];
    public double TotalLength { get; set; }

    public double MinX { get; set; } = double.MaxValue;
    public double MinY { get; set; } = double.MaxValue;
    public double MinZ { get; set; } = double.MaxValue;
    public double MaxX { get; set; } = double.MinValue;
    public double MaxY { get; set; } = double.MinValue;
    public double MaxZ { get; set; } = double.MinValue;

    public void Expand(double x, double y, double z)
    {
        if (x < MinX) MinX = x;
        if (y < MinY) MinY = y;
        if (z < MinZ) MinZ = z;
        if (x > MaxX) MaxX = x;
        if (y > MaxY) MaxY = y;
        if (z > MaxZ) MaxZ = z;
    }

    public bool HasCutting => MinZ < -1e-6;
}
