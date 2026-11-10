using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// S4/S6: exact thresholds, presets and histogram count measured pages only; estimates join only on request.
public class MeasuredOnlyFilterTests
{
    private static async Task<(SqliteArchiveStore Store, List<StoredCapture> All)> Seed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-meas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(5), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures.ToList();
        NewPage Save(StoredCapture c, PageMetadata m) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"), new ParsedPage(null, null, "x", m), DateTimeOffset.UtcNow);
        await store.SavePagesAsync([
            Save(all[0], new PageMetadata(Comments: 10, EngagementScore: 100, Confidence: 0.6)),
            Save(all[1], new PageMetadata(Comments: 2, EngagementScore: 20, Confidence: 0.6)),
            Save(all[2], new PageMetadata(EngagementScore: 500, Confidence: 0.2))]);   // estimate with a big number
        return (store, all);
    }

    [Fact]
    public async Task Threshold_excludes_estimates_by_default()
    {
        var (store, all) = await Seed();
        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("engaged", "engaged", 1));
        Assert.Equal([all[0].Id, all[1].Id], page!.Captures.Select(c => c.Id));
    }

    [Fact]
    public async Task Include_estimates_adds_them_and_still_ranks_measured_first()
    {
        var (store, all) = await Seed();
        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("engaged", "engaged", 1, IncludeEstimates: true));
        // measured first, then the estimate, then the unsaved archive-signal rows (which only appear with the option)
        Assert.Equal([all[0].Id, all[1].Id, all[2].Id], page!.Captures.Select(c => c.Id).Take(3));
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task Presets_and_histogram_ignore_estimates_unless_included()
    {
        var (store, _) = await Seed();
        var measured = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("engaged", "engaged", 0));
        Assert.All(measured!.Presets!, p => Assert.True(p.Value <= 100));   // 500 (estimate) never sets a preset
        Assert.Equal(2, measured.Histogram!.Sum());
        var withEstimates = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("engaged", "engaged", 0, true));
        Assert.Equal(5, withEstimates!.Histogram!.Sum());
    }
}