using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Within-tier percentile (scoring.md step 4): an outlier count no longer dominates through its magnitude.
public class PercentileNormalizationTests
{
    [Fact]
    public async Task Rank_not_magnitude_decides_between_a_huge_low_confidence_count_and_a_high_confidence_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-pct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(5), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;
        long[] comments = [1000, 10, 1, 2, 3];
        double[] conf = [0.5, 1.0, 1.0, 1.0, 1.0];
        var pages = all.Select((c, i) => new NewPage(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"),
            new ParsedPage(null, null, "x", new PageMetadata(Comments: comments[i], EngagementScore: PageMetadata.Engagement(null, null, comments[i], null, null), Confidence: conf[i])),
            DateTimeOffset.UtcNow)).ToList();
        await store.SavePagesAsync(pages);

        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("commented", "none", 0));

        // percentile * confidence weight: 10 comments -> 0.8 * 1.0 beats 1000 comments -> 1.0 * 0.75
        Assert.Equal([all[1].Id, all[0].Id, all[4].Id, all[3].Id, all[2].Id], page!.Captures.Select(c => c.Id).ToArray());
    }
}