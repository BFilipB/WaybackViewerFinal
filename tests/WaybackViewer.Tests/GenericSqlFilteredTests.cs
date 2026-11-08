using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;
using WaybackViewer.Storage.Sql;

namespace WaybackViewer.Tests;

/// The provider-neutral store must return exactly what the SQLite store returns for filtered, content and popularity
/// pages (the SQL is shared). Run against SQLite through SqliteDialect; PostgreSQL differs only in the dialect hooks.
public class GenericSqlFilteredTests
{
    private static async Task<(SqliteArchiveStore Sqlite, SqlArchiveStore Generic)> Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-gf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var caps = SampleData.Captures(30);
        var pages = caps.Take(20).Select((c, i) =>
        {
            var html = SampleData.Html(c.Original, i % 3 == 0 ? "the quick brown fox" : "nothing here");
            return new NewPage(c, HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow);
        }).ToList();

        var sqlite = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "a.db")}", new HtmlFileStore(Path.Combine(dir, "ha")));
        var generic = new SqlArchiveStore(new SqliteDialect(), $"Data Source={Path.Combine(dir, "b.db")}", new HtmlFileStore(Path.Combine(dir, "hb")));
        foreach (var store in new IArchiveStore[] { sqlite, generic })
        {
            await store.InitializeAsync();
            await store.SaveSearchAsync("a.com/", "all", caps, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            await store.SavePagesAsync(pages);
        }
        return (sqlite, generic);
    }

    private static void SamePage(FilteredPage? a, FilteredPage? b)
    {
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Total, b!.Total);
        Assert.Equal(a.Captures.Select(c => (c.Capture.Key, c.Ready)), b.Captures.Select(c => (c.Capture.Key, c.Ready)));
        Assert.Equal(a.UnsavedHtmlTotal, b.UnsavedHtmlTotal);
        Assert.Equal(a.UnsavedHtmlInView, b.UnsavedHtmlInView);
        Assert.Equal(a.HtmlTotal, b.HtmlTotal);
        Assert.Equal(a.Facets.Years.Select(y => (y.Value, y.Count, y.Saved)), b.Facets.Years.Select(y => (y.Value, y.Count, y.Saved)));
        Assert.Equal(a.Facets.UniqueCount, b.Facets.UniqueCount);
        Assert.Equal(a.Histogram, b.Histogram);
        Assert.Equal(a.Presets, b.Presets);
        Assert.Equal(a.PopularityExcluded, b.PopularityExcluded);
    }

    [Fact]
    public async Task Filtered_pages_match_the_sqlite_store()
    {
        var (s, g) = await Make();
        foreach (var f in new[] { new CaptureFilter(), new CaptureFilter(Category: "Pages"), new CaptureFilter(Unique: true), new CaptureFilter(Status: "200") })
            SamePage(await s.GetFilteredPageAsync("a.com/", "all", f, 2, 7), await g.GetFilteredPageAsync("a.com/", "all", f, 2, 7));
    }

    [Fact]
    public async Task Content_search_matches_the_sqlite_store()
    {
        var (s, g) = await Make();
        var a = await s.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(), 1, 50, "quick fox");
        var b = await g.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(), 1, 50, "quick fox");
        Assert.NotEmpty(b!.Captures);
        SamePage(a, b);
    }

    [Fact]
    public async Task Popularity_pages_match_the_sqlite_store()
    {
        var (s, g) = await Make();
        foreach (var q in new[] { new PopularityQuery("popular", "none", 0), new PopularityQuery("newest", "engaged", 0), new PopularityQuery("score", "engaged", 20) })
        {
            var a = await s.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 9, null, q);
            var b = await g.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 9, null, q);
            SamePage(a, b);
            Assert.Equal(a!.Popularity!.Keys.OrderBy(x => x), b!.Popularity!.Keys.OrderBy(x => x));
        }
    }

    [Fact]
    public async Task Unsaved_batches_page_through_the_same_captures()
    {
        var (s, g) = await Make();
        var a = await s.GetUnsavedHtmlBatchAsync("a.com/", "all", new CaptureFilter(), null, 4);
        var b = await g.GetUnsavedHtmlBatchAsync("a.com/", "all", new CaptureFilter(), null, 4);
        Assert.Equal(a.Select(c => c.Capture.Key), b.Select(c => c.Capture.Key));
        var next = await g.GetUnsavedHtmlBatchAsync("a.com/", "all", new CaptureFilter(), b[^1], 4);
        Assert.DoesNotContain(next, c => b.Any(x => x.Id == c.Id));
    }

    [Fact]
    public async Task Existing_generic_databases_are_classified_on_first_use()
    {
        var (_, g) = await Make();
        var page = await g.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(Category: "Pages"), 1, 5);
        Assert.NotNull(page);
        Assert.True(page!.Total > 0);
    }
}
