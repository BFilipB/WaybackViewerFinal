using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Popularity ranking, thresholds, presets and histogram computed in SQL must match the in-memory rules.
public class SqlPopularityTests
{
    private static async Task<(SqliteArchiveStore Store, IReadOnlyList<StoredCapture> All)> Make(int count = 40)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-pop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(count), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;
        return (store, all);
    }

    private static List<long> Expected(IReadOnlyList<StoredCapture> all, int threshold = 0)
    {
        var meta = ArchiveSignals.Fill(all, new Dictionary<string, PageMetadata>());
        return all.Select(c => (c, m: meta[c.Capture.Key].EffectiveEngagementScore))
            .Where(x => x.m >= threshold)
            .OrderByDescending(x => x.m).ThenByDescending(x => x.c.Capture.Timestamp, StringComparer.Ordinal).ThenByDescending(x => x.c.Id)
            .Select(x => x.c.Id).ToList();
    }

    [Fact]
    public async Task Popular_order_matches_the_in_memory_ranking_across_pages()
    {
        var (store, all) = await Make();
        var expected = Expected(all);
        var got = new List<long>();
        for (var p = 1; p <= 4; p++)
        {
            var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), p, 10, null, new PopularityQuery("popular", "none", 0));
            Assert.NotNull(page);
            Assert.Equal(all.Count, page!.Total);
            got.AddRange(page.Captures.Select(c => c.Id));
        }
        Assert.Equal(expected, got);
    }

    [Fact]
    public async Task Threshold_filters_in_sql_and_reports_the_excluded_count()
    {
        var (store, all) = await Make();
        var meta = ArchiveSignals.Fill(all, new Dictionary<string, PageMetadata>());
        var threshold = (int)meta.Values.Select(m => m.EffectiveEngagementScore).OrderBy(x => x).ElementAt(all.Count / 2);
        var expected = Expected(all, threshold);

        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("score", "engaged", threshold));
        Assert.Equal(expected.Count, page!.Total);
        Assert.Equal(all.Count - expected.Count, page.PopularityExcluded);
        Assert.Equal(expected.OrderBy(x => x), page.Captures.Select(c => c.Id).OrderBy(x => x));
        Assert.All(page.Captures, c => Assert.True(page.Popularity!.ContainsKey(c.Capture.Key)));
    }

    [Fact]
    public async Task Histogram_and_presets_cover_every_capture()
    {
        var (store, all) = await Make();
        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 5, null, new PopularityQuery("newest", "engaged", 0));
        Assert.Equal(10, page!.Histogram!.Count);
        Assert.Equal(all.Count, page.Histogram.Sum());
        Assert.All(page.Presets!, p => Assert.True(p.Value > 0));
        Assert.Equal(5, page.Captures.Count);
    }

    [Fact]
    public async Task Missing_search_and_filter_falls_back_and_free_text_runs_in_sql()
    {
        var (store, _) = await Make();
        Assert.Null(await store.GetPopularPageAsync("none/", "all", new CaptureFilter(), 1, 5, null, new PopularityQuery("popular", "none", 0)));
        var text = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(Text: "zzzz-no-match"), 1, 5, null, new PopularityQuery("popular", "none", 0));   // free text now runs in SQL
        Assert.NotNull(text);
        Assert.Equal(0, text!.Total);
    }

    [Fact]
    public async Task Signals_are_rebuilt_when_the_list_is_refreshed()
    {
        var (store, all) = await Make(10);
        await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 5, null, new PopularityQuery("popular", "none", 0));
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(20), DateTimeOffset.UtcNow.AddMinutes(1));
        var page = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("popular", "none", 0));
        Assert.Equal(20, page!.Total);
        Assert.Equal(20, page.Captures.Select(c => c.Id).Distinct().Count());
    }
}
