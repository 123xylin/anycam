using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnyCAD.Foundation;

namespace AnyTubeNet.Models;

public sealed class AnyTubeDocument
{
    public AnyTubePipe? Pipe { get; set; }
    public AnyTubePipeContour? PipeContour { get; set; }
    public List<AnyTubeContour> Contours { get; set; } = [];
}

public sealed class AnyTubePipe
{
    public int PipeType { get; set; }
    public double R { get; set; }
    public double R1 { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double A { get; set; }
    public double B { get; set; }
    public double Theta { get; set; }
    public int Sides { get; set; }
    public double Thickness { get; set; }
    public double Length { get; set; }
    public double Offcut { get; set; }
    public string Material { get; set; } = string.Empty;
}

public sealed class AnyTubePipeContour
{
    public List<AnyTubeContourEdge> OuterContour { get; set; } = [];
    public List<AnyTubeContourEdge> InnerContour { get; set; } = [];
}

public sealed class AnyTubeContourEdge
{
    public int Type { get; set; }
    public Point2 SP { get; set; } = new();
    public Point2 EP { get; set; } = new();
    public Point2? Center { get; set; }
    public double Angle { get; set; }
}

public sealed class AnyTubeContour
{
    public string PartName { get; set; } = string.Empty;
    public int PartNumber { get; set; }
    public int Type { get; set; }
    public double Length { get; set; }
    public List<AnyTubeSegment> Segments { get; set; } = [];
}

public sealed class AnyTubeSegment
{
    public int Stage { get; set; }
    public int Type { get; set; }
    public int LineType { get; set; }
    public Point3 SP { get; set; } = new();
    public Point3 EP { get; set; } = new();
    public Point3? SGV { get; set; }
    public Point3? EGV { get; set; }

    public Vector3d Start => SP.ToVector3d();
    public Vector3d End => EP.ToVector3d();
    public bool IsCut => Stage == 1 && Type == 0;
    public bool IsRapid => Type == 1;
    public double SegmentLength => (End - Start).length();
}

public sealed class Point2
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    public Vector3 ToVector3AtY(double y) => new((float)X, (float)y, (float)Z);
    public Vector3d ToVector3dAtY(double y) => new(X, y, Z);
}

public sealed class Point3
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    public Vector3d ToVector3d() => new(X, Y, Z);
}

public readonly record struct TubePathSegment(
    Vector3d Start,
    Vector3d End,
    Vector3d StartDirection,
    Vector3d EndDirection,
    int Stage,
    int Type)
{
    public bool IsCut => Stage == 1 && Type == 0;
    public bool IsMachining => Type == 0;
    public bool IsRapid => Type == 1;
    public double Length => (End - Start).length();

    public Vector3d Interpolate(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        return Start + (End - Start) * ratio;
    }

    public Vector3d InterpolateDirection(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        var direction = StartDirection + (EndDirection - StartDirection) * ratio;
        return direction.length() > 1e-9 ? direction.normalized() : StartDirection;
    }
}

public sealed record TubePathGroup(int ContourType, IReadOnlyList<TubePathSegment> Segments);

public sealed class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => Parse(reader.GetString()),
            _ => throw new JsonException($"Expected number or numeric string, got {reader.TokenType}.")
        };
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }

    private static double Parse(string? text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;

        throw new JsonException($"Invalid numeric value: {text}");
    }
}
