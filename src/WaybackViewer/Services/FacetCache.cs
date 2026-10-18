using System.Collections.Concurrent;
using WaybackViewer.Models;

namespace WaybackViewer.Services;

/// Caches the facet counts (timeline, categories, ...) of a loaded search so a page view does not recompute them.
/// The key contains the snapshot id and size, so a refreshed search never gets stale counts.
public sealed class FacetCache
{
    private const int MaxEntries = 64;
    private readonly ConcurrentDictionary<(long SearchId, int Count), CaptureFacets> cache = new();

    public CaptureFacets GetOrBuild(SearchSnapshot snapshot)
    {
        var key = (snapshot.SearchId, snapshot.Captures.Count);
        if (cache.TryGetValue(key, out var hit)) return hit;

        var built = CaptureFacets.Build(snapshot.Captures);
        if (cache.Count >= MaxEntries) cache.Clear();
        cache[key] = built;
        return built;
    }
}
