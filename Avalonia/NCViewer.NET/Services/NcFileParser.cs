using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AnyCAD.Foundation;
using NcViewerNet.Models;

namespace NcViewerNet.Services;

public static class NcFileParser
{
    private const double Epsilon = 1e-9;
    private static readonly Regex DiameterPattern = new(@"JD-?\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled);

    static NcFileParser()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Provider registration is best-effort; UTF-8 still works as a fallback.
        }
    }

    public static NcDocument Load(string path)
    {
        var document = new NcDocument();
        var segments = document.Segments;

        double cx = 0, cy = 0, cz = 0, cf = 0;
        int mode = 0; // 0 unknown, 1 rapid (G00), 2 feed (G01/G02/G03)
        bool absolute = true;
        bool hasPosition = false;

        var encoding = DetectEncoding(path);
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var lineComment = new StringBuilder();
            bool lineHasTool = false;

            double vx = 0, vy = 0, vz = 0;
            bool hasX = false, hasY = false, hasZ = false;

            ParseLine(line, ref mode, ref absolute, ref cf, ref lineHasTool,
                ref hasX, ref hasY, ref hasZ, ref vx, ref vy, ref vz, lineComment);

            if (lineHasTool)
            {
                var text = lineComment.ToString();
                document.ToolName = text;
                var match = DiameterPattern.Match(text);
                if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dia))
                    document.ToolDiameter = dia;
            }

            if (!hasX && !hasY && !hasZ)
                continue;

            double nx, ny, nz;
            if (absolute)
            {
                nx = hasX ? vx : cx;
                ny = hasY ? vy : cy;
                nz = hasZ ? vz : cz;
            }
            else
            {
                nx = hasX ? cx + vx : cx;
                ny = hasY ? cy + vy : cy;
                nz = hasZ ? cz + vz : cz;
            }

            if (hasPosition)
            {
                var start = new Vector3d(cx, cy, cz);
                var end = new Vector3d(nx, ny, nz);
                if ((end - start).length() > Epsilon)
                {
                    var motion = mode == 1 ? NcMotionType.Rapid : NcMotionType.Feed;
                    segments.Add(new NcSegment(start, end, motion, cf));
                }
            }

            document.Expand(nx, ny, nz);
            cx = nx; cy = ny; cz = nz;
            hasPosition = true;
        }

        if (segments.Count == 0)
            throw new InvalidDataException("NC 文件中未找到任何运动指令。");

        if (document.ToolDiameter <= 0)
            document.ToolDiameter = 8.0;

        BuildCumulative(document);
        return document;
    }

    private static void BuildCumulative(NcDocument document)
    {
        var segments = document.Segments;
        var cumulative = new double[segments.Count + 1];
        var sum = 0.0;
        for (var i = 0; i < segments.Count; i++)
        {
            cumulative[i] = sum;
            sum += segments[i].Length;
        }
        cumulative[segments.Count] = sum;
        document.Cumulative = cumulative;
        document.TotalLength = sum;
    }

    private static Encoding DetectEncoding(string path)
    {
        try
        {
            using var probe = File.OpenRead(path);
            var length = (int)Math.Min(probe.Length, 65536);
            var buffer = new byte[length];
            var read = probe.Read(buffer, 0, length);
            var utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            utf8Strict.GetString(buffer, 0, read);
            return utf8Strict;
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding(936);
            }
            catch
            {
                return new UTF8Encoding(false, false);
            }
        }
        catch
        {
            return new UTF8Encoding(false, false);
        }
    }

    private static void ParseLine(
        string line,
        ref int mode,
        ref bool absolute,
        ref double currentFeed,
        ref bool lineHasTool,
        ref bool hasX, ref bool hasY, ref bool hasZ,
        ref double vx, ref double vy, ref double vz,
        StringBuilder lineComment)
    {
        var n = line.Length;
        var i = 0;
        while (i < n)
        {
            var c = line[i];

            if (c == '(')
            {
                i++;
                while (i < n && line[i] != ')')
                {
                    lineComment.Append(line[i]);
                    i++;
                }
                if (i < n) i++;
                continue;
            }

            if (c == ';')
                break;

            if (char.IsLetter(c))
            {
                var letter = char.ToUpperInvariant(c);
                i++;
                while (i < n && line[i] == ' ') i++;

                var start = i;
                if (i < n && (line[i] == '+' || line[i] == '-')) i++;
                while (i < n && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                if (i < n && (line[i] == 'e' || line[i] == 'E'))
                {
                    i++;
                    if (i < n && (line[i] == '+' || line[i] == '-')) i++;
                    while (i < n && char.IsDigit(line[i])) i++;
                }

                if (i <= start)
                    continue;

                var span = line.AsSpan(start, i - start);
                if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                switch (letter)
                {
                    case 'G':
                        switch ((int)Math.Round(value))
                        {
                            case 0: mode = 1; break;
                            case 1: mode = 2; break;
                            case 2:
                            case 3: mode = 2; break;
                            case 90: absolute = true; break;
                            case 91: absolute = false; break;
                        }
                        break;
                    case 'X': hasX = true; vx = value; break;
                    case 'Y': hasY = true; vy = value; break;
                    case 'Z': hasZ = true; vz = value; break;
                    case 'F': currentFeed = value; break;
                    case 'T': lineHasTool = true; break;
                }
                continue;
            }

            i++;
        }
    }
}
