using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Pins the "engaged" ranking (log-scaled, S2/S3) of a mixed fixture (docs/popularity/scoring.md). The S2/S3 log-scaling change
/// reorders it on purpose: update EXPECTED together with the before/after shown to the user, never silently.
public class PopularitySnapshotTests
{
    [Fact]
    public async Task Engaged_ranking_snapshot_of_a_mixed_fixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(8), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;

        NewPage Save(StoredCapture c, PageMetadata m) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"), new ParsedPage(null, null, "x", m), DateTimeOffset.UtcNow);
        var views5000 = all[0]; var likes100 = all[1]; var score50 = all[2]; var comments10 = all[3]; var zero = all[4]; var estimate = all[5];
        await store.SavePagesAsync([
            Save(views5000, new PageMetadata(Views: 5000, EngagementScore: PageMetadata.Engagement(5000, null, null, null, null), Confidence: 0.6)),
            Save(likes100, new PageMetadata(Likes: 100, EngagementScore: PageMetadata.Engagement(null, 100, null, null, null), Confidence: 0.6)),
            Save(score50, new PageMetadata(Score: 50, EngagementScore: PageMetadata.Engagement(null, null, null, null, 50), Confidence: 0.6)),
            Save(comments10, new PageMetadata(Comments: 10, EngagementScore: PageMetadata.Engagement(null, null, 10, null, null), Confidence: 0.6)),
            Save(zero, new PageMetadata(Comments: 0, EngagementScore: 0, Confidence: 0.6)),
            Save(estimate, new PageMetadata(EngagementScore: 250, Confidence: 0.2))]);

        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("engaged", "none", 0));

        // EXPECTED (S2/S3 log scaling; before: views5000, likes100, score50, comments10, zero, estimate). Measured still outranks estimate.
        long[] expected = [likes100.Id, score50.Id, comments10.Id, views5000.Id, zero.Id, estimate.Id];
        Assert.Equal(expected, page!.Captures.Select(c => c.Id).Take(6));
    }
}
