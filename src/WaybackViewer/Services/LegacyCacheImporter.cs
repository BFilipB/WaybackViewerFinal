using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

public sealed record ImportReport(int Files, int CaptureLists, int Captures, int Imported, int Invalid, int Missing);

/// One-time migration of the old App_Data/wayback-cache/*.json files (v1 file cache) into the database.
/// Old entries are named sha256("html|timestamp|url").json, so each capture listed in a saved capture list
/// can be matched to its file even though the file itself does not contain the key.
public sealed class LegacyCacheImporter(IArchiveStore store)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record LegacyEntry(string Value, DateTimeOffset ExpiresUtc);
    private sealed record LegacyCaptureList(List<Capture> Captures, DateTimeOffset CachedAt);

    public async Task<ImportReport> ImportAsync(string directory, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Folder not found: {directory}");

        var files = Directory.EnumerateFiles(directory, "*.json").ToList();
        var names = new HashSet<string>(files.Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);

        // 1. find the saved capture lists (cheap peek at the first characters of each file)
        var captures = new Dictionary<string, Capture>(StringComparer.Ordinal);
        var lists = 0;
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!LooksLikeCaptureList(path)) continue;

            var list = TryReadList(path);
            if (list is null) continue;
            lists++;
            foreach (var c in list.Captures) captures[c.Key] = c;
        }
        progress?.Report($"Found {lists} capture list(s) with {captures.Count} captures.");

        // 2. import the HTML belonging to those captures
        var batch = new List<NewPage>(50);
        int imported = 0, invalid = 0, missing = 0, seen = 0;

        foreach (var capture in captures.Values.OrderBy(c => c.Timestamp, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            var fileName = FileNameFor("html|" + capture.Key);
            if (!names.Contains(fileName)) { missing++; continue; }

            var html = TryReadHtml(Path.Combine(directory, fileName));
            if (html is null || FrameHtml.LooksBinary(html) || !html.Contains('<')) { invalid++; continue; }

            batch.Add(new NewPage(capture, HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow));
            if (batch.Count >= 50)
            {
                await store.SavePagesAsync(batch.ToArray(), ct);
                imported += batch.Count;
                batch.Clear();
                progress?.Report($"Imported {imported} / {captures.Count} ...");
            }
        }

        if (batch.Count > 0)
        {
            await store.SavePagesAsync(batch.ToArray(), ct);
            imported += batch.Count;
        }

        return new ImportReport(files.Count, lists, captures.Count, imported, invalid, missing);
    }

    /// Same hashing as the old WaybackCache: lower-case hex SHA-256 of the key + ".json".
    public static string FileNameFor(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json";

    private static bool LooksLikeCaptureList(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            var buffer = new char[160];
            var n = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, n).Contains("Captures", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    private static LegacyCaptureList? TryReadList(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<LegacyEntry>(File.ReadAllText(path), Json);
            return entry is null ? null : JsonSerializer.Deserialize<LegacyCaptureList>(entry.Value, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static string? TryReadHtml(string path)
    {
        try { return JsonSerializer.Deserialize<LegacyEntry>(File.ReadAllText(path), Json)?.Value; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}
