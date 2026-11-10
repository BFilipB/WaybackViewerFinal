using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class ConfidenceWeightTests
{
    [Fact]
    public async Task Equal_counts_rank_by_confidence_and_a_big_gap_still_wins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-conf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(3), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;
        NewPage Save(StoredCapture c, long comments, double conf) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"),
                new ParsedPage(null, null, "x", new PageMetadata(Comments: comments, EngagementScore: PageMetadata.Engagement(null, null, comments, null, null), Confidence: conf)), DateTimeOffset.UtcNow);
        await store.SavePagesAsync([Save(all[0], 50, 0.5), Save(all[1], 50, 1.0), Save(all[2], 5, 1.0)]);

        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("commented", "none", 0));

        Assert.Equal([all[1].Id, all[0].Id, all[2].Id], page!.Captures.Select(c => c.Id).ToArray());
    }
}