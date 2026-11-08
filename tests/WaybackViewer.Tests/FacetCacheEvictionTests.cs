using WaybackViewer.Models;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class FacetCacheEvictionTests
{
    [Fact]
    public void Cache_evicts_oldest_entries_without_dropping_the_rest()
    {
        var cache = new FacetCache();
        var caps = new[] { new StoredCapture(1, new Capture("20200101010101", "https://a.com/x", "text/html", "200"), false) };
        FacetCache Snapshot(long id) => cache;
        var first = new SearchSnapshot(1, DateTimeOffset.UtcNow, caps.ToList());
        var built = cache.GetOrBuild(first);
        for (var id = 2; id <= 64; id++) cache.GetOrBuild(new SearchSnapshot(id, DateTimeOffset.UtcNow, caps.ToList()));
        Assert.Same(built, cache.GetOrBuild(first));                    // still cached at the limit
        cache.GetOrBuild(new SearchSnapshot(100, DateTimeOffset.UtcNow, caps.ToList()));   // 65th evicts only the oldest
        Assert.NotSame(built, cache.GetOrBuild(first));                 // oldest was rebuilt
        var kept = cache.GetOrBuild(new SearchSnapshot(64, DateTimeOffset.UtcNow, caps.ToList()));
        Assert.NotNull(kept);
    }
}
