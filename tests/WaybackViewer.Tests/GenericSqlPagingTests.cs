using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;
using WaybackViewer.Storage.Sql;

namespace WaybackViewer.Tests;

public class GenericSqlPagingTests
{
    [Fact]
    public async Task Generic_store_pages_in_sql_when_cold_and_matches_the_cached_page()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-gpage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        SqlArchiveStore Make() => new(new SqliteDialect(), $"Data Source={Path.Combine(dir, "g.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        var store = Make();
        await store.InitializeAsync();
        var caps = SampleData.Captures(25);
        await store.SaveSearchAsync("a.com/", "html200", caps, DateTimeOffset.UtcNow);
        var html = SampleData.Html(caps[3].Original, "x");
        await store.SavePagesAsync([new NewPage(caps[3], HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow)]);

        var cold = Make();
        await cold.InitializeAsync();
        var sql = await cold.GetSearchPageAsync("a.com/", "html200", 0, 25);
        await cold.GetSearchAsync("a.com/", "html200");
        var mem = await cold.GetSearchPageAsync("a.com/", "html200", 0, 25);

        Assert.Equal(25, sql!.TotalCount);
        Assert.Equal(mem!.Captures.Select(c => (c.Capture.Key, c.Ready)), sql.Captures.Select(c => (c.Capture.Key, c.Ready)));
        Assert.Equal(1, sql.Captures.Count(c => c.Ready));
        Assert.Equal(10, (await Make().GetSearchPageAsync("a.com/", "html200", 5, 10))!.Captures.Count);
        Assert.Null(await Make().GetSearchPageAsync("none.com/", "html200", 0, 10));
    }
}
