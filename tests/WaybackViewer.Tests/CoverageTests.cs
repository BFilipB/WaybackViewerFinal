using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Coverage line: how many captures in the view have measured counts (tier 2) out of all in the view.
public class CoverageTests
{
    [Fact]
    public async Task Coverage_counts_measured_rows_against_all_rows_in_view()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(5), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures.ToList();
        NewPage Save(StoredCapture c, PageMetadata m) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"), new ParsedPage(null, null, "x", m), DateTimeOffset.UtcNow);
        await store.SavePagesAsync([
            Save(all[0], new PageMetadata(Comments: 10, EngagementScore: 100, Confidence: 0.6)),
            Save(all[1], new PageMetadata(Comments: 0, EngagementScore: 0, Confidence: 0.6)),
            Save(all[2], new PageMetadata(EngagementScore: 5, Confidence: 0.2))]);

        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("engaged", "engaged", 0));

        Assert.Equal(2, page!.CoverageMeasured);   // explicit zero counts as measured
        Assert.Equal(5, page.CoverageTotal);
        Assert.Equal(2, page.MetricCoverage!["comments"]);   // 10 and an explicit 0 are both measured comment counts
        Assert.Equal(0, page.MetricCoverage["views"]);       // nobody measured views: the UI disables that metric
        Assert.Equal(0, page.MetricCoverage["likes"]);
    }
}
