using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class SqlPagingTests
{
    [Fact]
    public async Task Cold_sql_page_matches_the_cached_page_including_saved_flags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string cs() => $"Data Source={Path.Combine(dir, "d.db")}";
        var files = new HtmlFileStore(Path.Combine(dir, "html"));
        var store = new SqliteArchiveStore(cs(), files);
        await store.InitializeAsync();
        var caps = SampleData.Captures(25);
        await store.SaveSearchAsync("a.com/", "html200", caps, DateTimeOffset.UtcNow);
        var html = SampleData.Html(caps[3].Original, "x");
        await store.SavePagesAsync([new NewPage(caps[3], HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow)]);

        var cold = new SqliteArchiveStore(cs(), files);           // nothing cached -> SQL path
        await cold.InitializeAsync();
        var sqlPage = await cold.GetSearchPageAsync("a.com/", "html200", 0, 25);

        await cold.GetSearchAsync("a.com/", "html200");           // now cached -> in-memory path
        var memPage = await cold.GetSearchPageAsync("a.com/", "html200", 0, 25);

        Assert.Equal(25, sqlPage!.TotalCount);
        Assert.Equal(memPage!.Captures.Select(c => (c.Capture.Key, c.Ready)), sqlPage.Captures.Select(c => (c.Capture.Key, c.Ready)));
        Assert.Equal(1, sqlPage.Captures.Count(c => c.Ready));
        var slice = await new SqliteArchiveStore(cs(), files).GetSearchPageAsync("a.com/", "html200", 5, 10);
        Assert.Equal(10, slice!.Captures.Count);
        Assert.Null(await new SqliteArchiveStore(cs(), files).GetSearchPageAsync("none.com/", "html200", 0, 10));
    }
}
