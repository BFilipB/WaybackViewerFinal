using System.Text.Json;
using WaybackViewer.Models;

namespace WaybackViewer.Parsing;

public static class CdxParser
{
    /// Parses the CDX "output=json" body: a header row followed by [timestamp, original, mimetype, statuscode] rows.
    public static List<Capture> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        var rows = JsonSerializer.Deserialize<List<string[]>>(json) ?? [];
        var result = new List<Capture>(rows.Count);
        foreach (var row in rows)
        {
            if (row is not { Length: >= 4 }) continue;
            if (row[0] == "timestamp") continue; // header
            result.Add(new Capture(row[0], row[1], row[2], row[3]));
        }
        return result;
    }
}
