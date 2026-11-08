using WaybackViewer.Models;
using WaybackViewer.Storage;
using WaybackViewer.Storage.Sql;

namespace WaybackViewer.Tests;

public class FreeTextUrlSqlTests
{
    private static readonly string[] Urls =
    [
        "https://example.com/blog/hello-world", "https://example.com/docs/guide", "https://example.com/Shop/Čaj",
        "https://example.com/blog/second-post", "https://example.com/about",
    ];

    private static async Task<(IArchiveStore, List<StoredCapture>)> Make(bool generic)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var files = new HtmlFileStore(Path.Combine(dir, "html"));
        IArchiveStore store = generic
            ? new SqlArchiveStore(new SqliteDialect(), $"Data Source={Path.Combine(dir, "g.db")}", files)
            : new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", files);
        await store.InitializeAsync();
        var caps = Enumerable.Range(0, 40).Select(i =>
            new Capture(new DateTime(2020, 1, 1).AddMinutes(i).ToString("yyyyMMddHHmmss"), Urls[i % Urls.Length], "text/html", "200")).ToArray();
        await store.SaveSearchAsync("example.com/", "all", caps, DateTimeOffset.UtcNow);
        return (store, caps.Select(c => new StoredCapture(0, c, false)).ToList());
    }

    [Theory]
    [InlineData(false, "blog")]
    [InlineData(true, "blog")]
    [InlineData(false, "caj")]
    [InlineData(true, "guide docs")]
    [InlineData(false, "nomatchhere")]
    public async Task Free_text_url_filter_pages_in_sql_like_the_in_memory_matcher(bool generic, string text)
    {
        var (store, all) = await Make(generic);
        var f = new CaptureFilter(Text: text);
        var expected = all.Where(c => SearchTextMatcher.Matches(text, c.Capture.Original)).Select(c => c.Capture.Key).OrderBy(k => k).ToList();
        var seen = new List<string>();
        for (var p = 1; p <= Math.Max(1, (int)Math.Ceiling(expected.Count / 3.0)); p++)
        {
            var page = await store.GetFilteredPageAsync("example.com/", "all", f, p, 3);
            Assert.NotNull(page);
            Assert.Equal(expected.Count, page!.Total);
            Assert.True(page.Captures.Count <= 3);
            seen.AddRange(page.Captures.Select(c => c.Capture.Key));
        }
        Assert.Equal(expected, seen.OrderBy(k => k).ToList());
    }
}


