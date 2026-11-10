using System.Collections.Concurrent;
using WaybackViewer.Models;

namespace WaybackViewer.Services;

/// Caches the facet counts (timeline, categories, ...) of a loaded search so a page view does not recompute them.
/// The key contains the snapshot id and size, so a refreshed search never gets stale counts.
public sealed class FacetCache(Microsoft.Extensions.Logging.ILogger<FacetCache>? logger = null)
{
    private readonly Microsoft.Extensions.Logging.ILogger log = (Microsoft.Extensions.Logging.ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private const int MaxEntries = 64;
    private readonly ConcurrentQueue<(long SearchId, int Count)> order = new();
    private readonly ConcurrentDictionary<(long SearchId, int Count), CaptureFacets> cache = new();

    // ---- filtered views --------------------------------------------------------------------------------
    // "One per thread" over 100,000 captures costs about 100 ms. Paging through the result, opening the export or
    // the slideshow all ask for the same view, so the last few views are kept (per loaded list, per filter).
    private const int MaxViews = 8;
    private readonly object viewLock = new();
    private readonly List<(SearchSnapshot Snapshot, CaptureFilter Filter, FilteredView View)> views = new();

    public FilteredView GetOrApply(SearchSnapshot snapshot, CaptureFilter filter)
    {
        lock (viewLock)
        {
            for (var i = 0; i < views.Count; i++)
            {
                var entry = views[i];
                if (!ReferenceEquals(entry.Snapshot, snapshot) || entry.Filter != filter) continue;
                views.RemoveAt(i);
                views.Add(entry);   // most recently used goes last
                return entry.View;
            }
        }

        var applyStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var items = CaptureFacets.Apply(snapshot.Captures, filter);
        var sizes = filter.Unique ? CaptureFacets.GroupSizes(snapshot.Captures, filter) : null;
        var view = new FilteredView(items, sizes);
        log.LogInformation("facets.view_built searchId={SearchId} captures={Captures} items={Items} unique={Unique} ms={Ms:F0}", snapshot.SearchId, snapshot.Captures.Count, items.Count, filter.Unique, System.Diagnostics.Stopwatch.GetElapsedTime(applyStart).TotalMilliseconds);
        lock (viewLock)
        {
            views.Add((snapshot, filter, view));
            while (views.Count > MaxViews) views.RemoveAt(0);
        }
        return view;
    }

    public CaptureFacets GetOrBuild(SearchSnapshot snapshot)
    {
        var key = (snapshot.SearchId, snapshot.Captures.Count);
        if (cache.TryGetValue(key, out var hit)) return hit;

        var buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var built = CaptureFacets.Build(snapshot.Captures);
        log.LogInformation("facets.built searchId={SearchId} captures={Captures} cached={Cached} ms={Ms:F0}", snapshot.SearchId, snapshot.Captures.Count, cache.Count, System.Diagnostics.Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds);
        while (cache.Count >= MaxEntries && order.TryDequeue(out var oldest)) cache.TryRemove(oldest, out _);   // evict oldest, never drop everything
        if (cache.TryAdd(key, built)) order.Enqueue(key);
        return built;
    }
}

/// The captures that pass a filter (newest first) and, for "one per thread", how many captures each thread has.
public sealed record FilteredView(List<StoredCapture> Items, IReadOnlyDictionary<string, int>? GroupSizes);
