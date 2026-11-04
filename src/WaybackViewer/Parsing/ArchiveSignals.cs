using WaybackViewer.Models;

namespace WaybackViewer.Parsing;

/// Download-free, site-independent popularity fallback built only from the capture list:
/// how often the archive captured a URL, and how many other captured URLs sit underneath it (a hub).
public static class ArchiveSignals
{
    public static IReadOnlyDictionary<string, PageMetadata> Fill(
        IReadOnlyList<StoredCapture> captures, IReadOnlyDictionary<string, PageMetadata> metadata)
    {
        var perUrl = captures.GroupBy(c => c.Capture.Original, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var below = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in perUrl.Keys)
        {
            var p = url.TrimEnd('/');
            for (var i = p.LastIndexOf('/'); i > 8; i = p.LastIndexOf('/'))
            {
                p = p[..i];
                below[p] = below.GetValueOrDefault(p) + 1;
            }
        }
        var raw = new Dictionary<string, long>();
        foreach (var c in captures)
        {
            if (metadata.ContainsKey(c.Capture.Key)) continue;
            var hub = below.GetValueOrDefault(c.Capture.Original.TrimEnd('/'));
            raw[c.Capture.Key] = Math.Min(perUrl[c.Capture.Original] * 5L, 60) + Math.Min(hub * 2L, 40);
        }

        // Rank within each site, so a quiet site's best pages still count as popular on that site.
        var merged = new Dictionary<string, PageMetadata>(metadata);
        foreach (var site in captures.Where(c => raw.ContainsKey(c.Capture.Key)).GroupBy(c => Host(c.Capture.Original)))
        {
            var values = site.Select(c => raw[c.Capture.Key]).OrderBy(v => v).ToArray();
            foreach (var c in site)
            {
                var lower = LowerBound(values, raw[c.Capture.Key]); var upper = LowerBound(values, raw[c.Capture.Key] + 1);
                var score = 1 + (long)(99.0 * ((lower + upper) / 2.0) / Math.Max(values.Length, 1));
                merged[c.Capture.Key] = new PageMetadata(EngagementScore: score, Confidence: 0.1);
            }
        }
        return merged;
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";

    private static int LowerBound(long[] sorted, long value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
