using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// "Least ..." sorts: lowest metric first, but measured rows still come before estimates and archive-only rows.
public class LeastSortTests
{
    [Fact]
    public async Task Least_commented_lists_lowest_measured_first_and_keeps_tier_order()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-least-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(5), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;

        NewPage Save(StoredCapture c, PageMetadata m) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"), new ParsedPage(null, null, "x", m), DateTimeOffset.UtcNow);
        var ten = all[0]; var two = all[1]; var zero = all[2]; var estimate = all[3];
        await store.SavePagesAsync([
            Save(ten, new PageMetadata(Comments: 10, EngagementScore: 40, Confidence: 0.6)),
            Save(two, new PageMetadata(Comments: 2, EngagementScore: 8, Confidence: 0.6)),
            Save(zero, new PageMetadata(Comments: 0, EngagementScore: 0, Confidence: 0.6)),
            Save(estimate, new PageMetadata(EngagementScore: 1, Confidence: 0.2))]);

        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("least-commented", "none", 0));
        var ids = page!.Captures.Select(c => c.Id).ToList();

        Assert.Equal([zero.Id, two.Id, ten.Id, estimate.Id], ids.Take(4));
        var most = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("commented", "none", 0));
        Assert.Equal([ten.Id, two.Id, zero.Id, estimate.Id], most!.Captures.Select(c => c.Id).Take(4));
    }

    [Fact]
    public async Task Least_sort_is_offered_and_renders_through_the_controller()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("example.com/", SampleData.Captures(4));
        await host.GetAsync("/?url=example.com/");

        var html = await host.GetAsync("/?url=example.com/&sort=least-commented");

        Assert.Contains("value=\"least-commented\"", html);
        Assert.Contains("Least commented", html);
        Assert.DoesNotContain("Enter a valid URL", html);
    }
}
