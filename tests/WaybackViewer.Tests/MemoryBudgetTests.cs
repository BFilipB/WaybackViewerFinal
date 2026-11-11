using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// In-memory caches are bounded by size, not only by entry count, so huge lists cannot fill RAM.
public class MemoryBudgetTests
{
    private static SearchSnapshot Snapshot(long id, int count)
    {
        var caps = new StoredCapture[count];
        var capture = SampleData.Capture(0);
        for (var i = 0; i < count; i++) caps[i] = new StoredCapture(i + 1, capture, false);
        return new SearchSnapshot(id, DateTimeOffset.UtcNow, caps);
    }

    [Fact]
    public void Filtered_views_are_evicted_by_total_items()
    {
        var cache = new FacetCache();
        var big = Snapshot(1, FacetCache.MaxViewItems / 2 + 1);
        var other = Snapshot(2, FacetCache.MaxViewItems / 2 + 1);
        var first = cache.GetOrApply(big, new CaptureFilter());
        Assert.Same(first, cache.GetOrApply(big, new CaptureFilter()));   // small enough: cached

        cache.GetOrApply(other, new CaptureFilter());                      // together over budget: oldest evicted
        Assert.NotSame(first, cache.GetOrApply(big, new CaptureFilter()));
    }

    [Fact]
    public void Newest_view_is_kept_even_when_alone_over_budget()
    {
        var cache = new FacetCache();
        var huge = Snapshot(3, FacetCache.MaxViewItems + 10);
        var view = cache.GetOrApply(huge, new CaptureFilter());
        Assert.Same(view, cache.GetOrApply(huge, new CaptureFilter()));
    }

    [Fact]
    public void Sqlite_connections_use_bounded_memory_settings()
    {
        Assert.Contains("temp_store=FILE", SqliteArchiveStore.ConnectionPragmas);
        Assert.Contains("cache_size=-8192", SqliteArchiveStore.ConnectionPragmas);
        Assert.Contains("mmap_size=0", SqliteArchiveStore.ConnectionPragmas);
    }

    [Fact]
    public async Task Search_cache_still_serves_lists_after_budget_eviction()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-mb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "m.db")}", new HtmlFileStore(Path.Combine(dir, "h")));
        await store.InitializeAsync();
        for (var s = 0; s < 6; s++)
            await store.SaveSearchAsync($"site{s}.com/", "all", SampleData.Captures(20), DateTimeOffset.UtcNow);
        for (var s = 0; s < 6; s++)
            Assert.Equal(20, (await store.GetSearchAsync($"site{s}.com/", "all"))!.Captures.Count);
        Assert.Equal(20, (await store.GetSearchAsync("site0.com/", "all"))!.Captures.Count);   // evicted, reloaded from SQL
    }
}
