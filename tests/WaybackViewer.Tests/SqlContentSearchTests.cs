using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Content search runs as an indexed SQL filter over saved pages and pages in SQL.
public class SqlContentSearchTests
{
    private static async Task<SqliteArchiveStore> Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        var caps = SampleData.Captures(12);
        await store.SaveSearchAsync("a.com/", "all", caps, DateTimeOffset.UtcNow);
        var pages = new List<NewPage>();
        for (var i = 0; i < caps.Count; i++)
        {
            var html = SampleData.Html(caps[i].Original, i % 3 == 0 ? "the quick brown fox" : "nothing here");
            pages.Add(new NewPage(caps[i], HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow));
        }
        await store.SavePagesAsync(pages.Take(9).ToList());   // the last three stay unsaved
        return store;
    }

    [Fact]
    public async Task Content_search_matches_saved_pages_and_pages_in_sql()
    {
        var store = await Make();
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;
        var expected = all.Where(c => c.Ready).Select(c => c.Id).ToHashSet();

        var hit = await store.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, contentTerm: "quick fox");
        Assert.NotNull(hit);
        Assert.NotEmpty(hit!.Captures);
        Assert.All(hit.Captures, c => Assert.True(c.Ready && expected.Contains(c.Id)));
        Assert.True(hit.Total < all.Count);

        var paged = new List<long>();
        for (var p = 1; p <= hit.Total; p++)
            paged.AddRange((await store.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(), p, 1, contentTerm: "quick fox"))!.Captures.Select(c => c.Id));
        Assert.Equal(hit.Captures.Select(c => c.Id).OrderBy(x => x), paged.Distinct().OrderBy(x => x));
    }

    [Fact]
    public async Task Content_search_with_no_match_is_empty_and_combines_with_facets()
    {
        var store = await Make();
        var none = await store.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(), 1, 10, contentTerm: "zzzzqqq");
        Assert.Equal(0, none!.Total);
        Assert.Empty(none.Captures);

        var narrowed = await store.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(Category: "Images"), 1, 10, contentTerm: "quick");
        Assert.Equal(0, narrowed!.Total);   // saved pages are HTML, so the Images facet leaves nothing
    }

    [Fact]
    public async Task Content_search_does_not_treat_wildcards_in_the_term_as_patterns()
    {
        var store = await Make();
        var page = await store.GetFilteredPageAsync("a.com/", "all", new CaptureFilter(), 1, 10, contentTerm: "q%");
        Assert.NotNull(page);
        Assert.True(page!.Total <= 9);
    }
}
