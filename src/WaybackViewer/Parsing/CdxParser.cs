using System.Text.Json;
using WaybackViewer.Models;

namespace WaybackViewer.Parsing;

public static class CdxParser
{
    /// Parses the CDX "output=json" body: a header row followed by [timestamp, original, mimetype, statuscode] rows.
    public static List<Capture> Parse(string? json) => Parse(json, out _);

    /// Same as Parse, but also reports how many malformed rows (null/short) were skipped so callers can log data loss.
    public static List<Capture> Parse(string? json, out int skipped)
    {
        skipped = 0;
        if (string.IsNullOrWhiteSpace(json)) return [];
        var rows = JsonSerializer.Deserialize<List<string[]>>(json) ?? [];
        var result = new List<Capture>(rows.Count);
        foreach (var row in rows)
        {
            if (row is not { Length: >= 4 }) { skipped++; continue; }
            if (row[0] == "timestamp") continue; // header
            result.Add(new Capture(row[0], row[1], row[2], row[3]));
        }
        return result;
    }
}
