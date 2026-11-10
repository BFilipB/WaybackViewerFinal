using System.Collections.Concurrent;
using WaybackViewer.Models;

namespace WaybackViewer.Services;

/// Bounded per-host aggregation of extraction coverage (measured fields, sources, parse time). Drained periodically
/// into `extract.summary` lines so the log shows which sites lack which fields without reading per-page Debug lines.
public static class ExtractionStats
{
    private const int MaxHosts = 500;
    private static readonly ConcurrentDictionary<string, Counter> hosts = new(StringComparer.OrdinalIgnoreCase);

    public sealed class Counter
    {
        public int Pages, Views, Likes, Comments, Published, Author, None, Slow, Measured, Estimated;
        public double ParseMs;
        public readonly Dictionary<string, int> Sources = new(StringComparer.Ordinal);
    }

    /// Host of a capture key ("timestamp|original"); "unknown" when it cannot be parsed.
    public static string HostOfKey(string key)
    {
        var bar = key.IndexOf('|');
        var url = bar >= 0 ? key[(bar + 1)..] : key;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "unknown";
    }

    public static void Record(string key, PageMetadata? m, double parseMs)
    {
        var host = HostOfKey(key);
        if (!hosts.ContainsKey(host) && hosts.Count >= MaxHosts) host = "other";   // bounded memory
        var c = hosts.GetOrAdd(host, _ => new Counter());
        lock (c)
        {
            c.Pages++;
            c.ParseMs += parseMs;
            if (parseMs > 500) c.Slow++;
            if (m?.Views is not null) c.Views++;
            if (m?.Likes is not null) c.Likes++;
            if (m?.Comments is not null) c.Comments++;
            if (m?.PublishedAt is not null) c.Published++;
            if (m?.Author is not null) c.Author++;
            if (m is null || (m.Views is null && m.Likes is null && m.Comments is null && m.Shares is null && m.Score is null && m.EngagementScore is not > 0)) c.None++;
            var hasCounts = m is not null && (m.Views is not null || m.Likes is not null || m.Comments is not null || m.Shares is not null || m.Score is not null);
            if (hasCounts) c.Measured++; else if (m?.EngagementScore is > 0) c.Estimated++;   // estimate = engagement only, no real counts
            var source = m?.Source ?? "none";
            c.Sources[source] = c.Sources.TryGetValue(source, out var n) ? n + 1 : 1;
        }
    }

    /// Clears all counters (test isolation).
    public static void Reset() => hosts.Clear();

    /// Writes and resets one `extract.summary` line per host seen since the last call. Returns the number of hosts written.
    public static int Flush(ILogger log)
    {
        var written = 0;
        foreach (var host in hosts.Keys.ToList())
        {
            if (!hosts.TryRemove(host, out var c)) continue;
            lock (c)
            {
                var sources = string.Join(',', c.Sources.OrderByDescending(s => s.Value).Select(s => $"{s.Key}:{s.Value}"));
                log.LogInformation("extract.summary host={Host} pages={Pages} views={Views} likes={Likes} comments={Comments} published={Published} author={Author} measured={Measured} estimated={Estimated} noPopularity={None} slow={Slow} avgParseMs={Avg:F1} sources={Sources}",
                    host, c.Pages, c.Views, c.Likes, c.Comments, c.Published, c.Author, c.Measured, c.Estimated, c.None, c.Slow, c.Pages > 0 ? c.ParseMs / c.Pages : 0, sources);
            }
            written++;
        }
        return written;
    }
}
