using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AnyTubeNet.Models;

namespace AnyTubeNet.Services;

public static class AnyTubeJsonLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    static AnyTubeJsonLoader()
    {
        JsonOptions.Converters.Add(new FlexibleDoubleConverter());
    }

    public static AnyTubeDocument Load(string fileName)
    {
        var text = File.ReadAllText(fileName);
        var document = JsonSerializer.Deserialize<AnyTubeDocument>(text, JsonOptions)
            ?? throw new InvalidDataException("The JSON file is empty.");

        Validate(document);
        return document;
    }

    private static void Validate(AnyTubeDocument document)
    {
        if (document.Pipe is null)
            throw new InvalidDataException("Missing Pipe section.");

        if (document.Contours.Count == 0)
            throw new InvalidDataException("Missing Contours section.");

        if (document.Contours.All(c => c.Segments.Count == 0))
            throw new InvalidDataException("Contours do not contain path segments.");

        var invalidSegment = document.Contours
            .SelectMany(c => c.Segments)
            .FirstOrDefault(s => s.SegmentLength <= 0);
        if (invalidSegment is not null)
            throw new InvalidDataException("Path contains a zero-length segment.");
    }
}
