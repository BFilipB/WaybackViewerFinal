using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// The SQL filtered/paged read must give exactly what the in-memory CaptureFacets code gives.
public class SqlFilteredPageTests
{
    private static readonly string[] Urls =
    [
        "reddit.com/r/a/comments/abc/thread_one/", "reddit.com/r/a/comments/abc/thread_one/def/",
        "reddit.com/r/a/comments/xyz/thread_two/", "reddit.com/r/b/", "reddit.com/user/bob/",
        "reddit.com/r/a/wiki/index", "reddit.com/r/a/comments/xyz/thread_two/?sort=new", "example.com/page.css",
        "example.com/logo.png"
    ];

    private static List<Capture> Seed()
    {
        var list = new List<Capture>();
        var n = 0;
        foreach (var year in new[] { "2019", "2021", "2023" })
            foreach (var url in Urls)
            {
                var mime = url.EndsWith(".css") ? "text/css" : url.EndsWith(".png") ? "image/png" : "text/html";
                var status = n % 5 == 0 ? "301" : n % 7 == 0 ? "404" : "200";
                list.Add(new Capture($"{year}{(n % 12) + 1:D2}{(n % 28) + 1:D2}120000", "https://www." + url, mime, status));
                n++;
            }
        return list;
    }

    private static async Task<(SqliteArchiveStore Store, List<StoredCapture> All)> Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-filt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        var caps = Seed();
        var snap = await store.SaveSearchAsync("reddit.com/", "all", caps, DateTimeOffset.UtcNow);
        var html = SampleData.Html(caps[0].Original, "x");
        await store.SavePagesAsync([new NewPage(caps[0], HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow)]);
        var all = (await store.GetSearchAsync("reddit.com/", "all"))!.Captures.ToList();
        return (store, all);
    }

    public static IEnumerable<object[]> Filters() =>
    [
        [new CaptureFilter(Year: "2021")],
        [new CaptureFilter(Category: "Pages")],
        [new CaptureFilter(Status: "200")],
        [new CaptureFilter(Section: "/r")],
        [new CaptureFilter(Kind: UrlClassifier.Thread)],
        [new CaptureFilter(Unique: true)],
        [new CaptureFilter(Year: "2023", Category: "Pages", Unique: true)],
        [new CaptureFilter(Kind: UrlClassifier.Thread, Unique: true)],
    ];

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Sql_page_matches_the_in_memory_filter(CaptureFilter f)
    {
        var (store, all) = await Make();
        var expected = CaptureFacets.Apply(all, f);
        const int size = 4;

        var seen = new List<StoredCapture>();
        for (var page = 1; page <= Math.Max(1, (int)Math.Ceiling(expected.Count / (double)size)); page++)
        {
            var sql = await store.GetFilteredPageAsync("reddit.com/", "all", f, page, size);
            Assert.NotNull(sql);
            Assert.Equal(expected.Count, sql!.Total);
            Assert.True(sql.Captures.Count <= size);
            seen.AddRange(sql.Captures);
        }
        Assert.Equal(expected.Select(c => (c.Capture.Key, c.Ready)), seen.Select(c => (c.Capture.Key, c.Ready)));
    }

    [Fact]
    public async Task Sql_facets_match_the_in_memory_facets()
    {
        var (store, all) = await Make();
        var expected = CaptureFacets.Build(all);
        var sql = (await store.GetFilteredPageAsync("reddit.com/", "all", new CaptureFilter(), 1, 5))!.Facets;

        Assert.Equal(expected.Years, sql.Years);
        Assert.Equal(expected.Categories, sql.Categories);
        Assert.Equal(expected.Statuses, sql.Statuses);
        Assert.Equal(expected.Sections, sql.Sections);
        Assert.Equal(expected.Kinds, sql.Kinds);
        Assert.Equal(expected.UniqueCount, sql.UniqueCount);
    }

    [Fact]
    public async Task Group_sizes_match_for_the_unique_view()
    {
        var (store, all) = await Make();
        var f = new CaptureFilter(Unique: true);
        var expected = CaptureFacets.GroupSizes(all, f);
        var sql = (await store.GetFilteredPageAsync("reddit.com/", "all", f, 1, 100))!;

        Assert.NotNull(sql.GroupSizes);
        foreach (var capture in sql.Captures)
        {
            var group = UrlClassifier.Classify(capture.Capture.Original).GroupKey;
            Assert.Equal(expected[group], sql.GroupSizes![group]);
        }
    }

    [Fact]
    public async Task Counts_and_unsaved_batches_cover_only_unsaved_html_and_page_through()
    {
        var (store, all) = await Make();
        var f = new CaptureFilter(Year: "2021");
        var page = (await store.GetFilteredPageAsync("reddit.com/", "all", f, 1, 3))!;
        var expectedUnsaved = all.Count(c => c.Capture.IsHtml && !c.Ready);
        Assert.Equal(expectedUnsaved, page.UnsavedHtmlTotal);
        Assert.Equal(all.Count(c => c.Capture.IsHtml), page.HtmlTotal);

        var got = new List<StoredCapture>();
        StoredCapture? cursor = null;
        while (true)
        {
            var batch = await store.GetUnsavedHtmlBatchAsync("reddit.com/", "all", f, cursor, 4);
            if (batch.Count == 0) break;
            got.AddRange(batch);
            cursor = batch[^1];
        }
        var expected = CaptureFacets.Apply(all, f).Where(c => c.Capture.IsHtml && !c.Ready)
            .OrderBy(c => c.Capture.Timestamp, StringComparer.Ordinal).ThenBy(c => c.Id).Select(c => c.Capture.Key);
        Assert.Equal(expected, got.Select(c => c.Capture.Key));
    }

    [Fact]
    public async Task Missing_search_returns_null_and_free_text_runs_in_sql()
    {
        var (store, _) = await Make();
        Assert.Null(await store.GetFilteredPageAsync("none.com/", "all", new CaptureFilter(), 1, 5));
        var text = await store.GetFilteredPageAsync("reddit.com/", "all", new CaptureFilter(Text: "zzzz-no-match"), 1, 5);   // free text now runs in SQL
        Assert.NotNull(text);
        Assert.Equal(0, text!.Total);
    }

    [Fact]
    public async Task Existing_databases_are_classified_on_first_open()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-filt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cs = $"Data Source={Path.Combine(dir, "d.db")}";
        var files = new HtmlFileStore(Path.Combine(dir, "html"));
        var first = new SqliteArchiveStore(cs, files);
        await first.InitializeAsync();
        await first.SaveSearchAsync("reddit.com/", "all", Seed(), DateTimeOffset.UtcNow);

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            conn.Open();
            using var wipe = conn.CreateCommand();
            wipe.CommandText = "UPDATE Captures SET Year = NULL, Category = NULL, Section = NULL, Kind = NULL, GroupKey = NULL";
            wipe.ExecuteNonQuery();
        }

        var reopened = new SqliteArchiveStore(cs, files);
        await reopened.InitializeAsync();
        var page = await reopened.GetFilteredPageAsync("reddit.com/", "all", new CaptureFilter(Year: "2019"), 1, 50);
        Assert.Equal(Urls.Length, page!.Total);
    }
}
