using Microsoft.Data.Sqlite;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class UrlSummaryStoreTests
{
    private static async Task<SqliteArchiveStore> SqliteAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-p5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, name)}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        return store;
    }

    private static async Task SeedAsync(IArchiveStore store)
    {
        var at = DateTimeOffset.UtcNow;
        await store.SaveSearchAsync("small.com/", "html200", SampleData.Captures(3), at.AddMinutes(-3));
        await store.SaveSearchAsync("big.com/", "html200", SampleData.Captures(9), at.AddMinutes(-2));
        await store.SaveSearchAsync("mid.com/", "html200", SampleData.Captures(6), at.AddMinutes(-1));
    }

    public static IEnumerable<object[]> Stores()
    {
        yield return new object[] { "sqlite" };
        yield return new object[] { "memory" };
    }

    private static async Task<IArchiveStore> CreateAsync(string kind) =>
        kind == "sqlite" ? await SqliteAsync("urls.db") : new InMemoryArchiveStore();

    private static UrlListQuery Q(string? search, string sort, string? after, long afterId, int limit) =>
        new(search, sort, after, afterId, limit);

    [Theory, MemberData(nameof(Stores))]
    public async Task Urls_are_ranked_by_capture_count(string kind)
    {
        var store = await CreateAsync(kind);
        await SeedAsync(store);

        var page = await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10));

        Assert.Equal(["big.com/", "mid.com/", "small.com/"], page.Items.Select(i => i.Prefix));
        Assert.Equal(9, page.Items[0].CaptureCount);
        Assert.False(page.HasMore);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Keyset_paging_visits_every_row_once_for_every_sort(string kind)
    {
        var store = await CreateAsync(kind);
        await SeedAsync(store);
        foreach (var sort in new[] { UrlSorts.Captures, UrlSorts.Recent, UrlSorts.Alpha })
        {
            var seen = new List<string>();
            string? after = null;
            long afterId = 0;
            for (var guard = 0; guard < 10; guard++)
            {
                var page = await store.GetUrlListAsync(Q(null, sort, after, afterId, 1));
                Assert.True(page.Items.Count <= 1);
                seen.AddRange(page.Items.Select(i => i.Prefix));
                if (!page.HasMore) break;
                after = UrlSorts.CursorKey(sort, page.Items[^1]);
                afterId = page.Items[^1].Id;
            }
            Assert.Equal(3, seen.Count);
            Assert.Equal(3, seen.Distinct().Count());
        }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Search_text_and_autocomplete_match_prefix_and_rank_by_captures(string kind)
    {
        var store = await CreateAsync(kind);
        var at = DateTimeOffset.UtcNow;
        await store.SaveSearchAsync("example.com/a/", "html200", SampleData.Captures(2), at);
        await store.SaveSearchAsync("example.com/b/", "html200", SampleData.Captures(8), at);
        await store.SaveSearchAsync("other.org/", "html200", SampleData.Captures(5), at);

        var suggestions = await store.SuggestUrlsAsync("example", 20);
        Assert.Equal(["example.com/b/", "example.com/a/"], suggestions.Select(s => s.Prefix));
        Assert.Single(await store.SuggestUrlsAsync("example", 1));
        Assert.Equal(["example.com/b/", "other.org/", "example.com/a/"], (await store.SuggestUrlsAsync("", 200)).Select(s => s.Prefix));
        Assert.Equal(["other.org/"], (await store.SuggestUrlsAsync("org", 200)).Select(s => s.Prefix));

        var filtered = await store.GetUrlListAsync(Q("other", UrlSorts.Alpha, null, 0, 10));
        Assert.Equal("other.org/", Assert.Single(filtered.Items).Prefix);

        // LIKE wildcards in user input are literals.
        if (kind == "sqlite") Assert.Empty((await store.GetUrlListAsync(Q("%", UrlSorts.Alpha, null, 0, 10))).Items);
    }

    [Fact]
    public async Task Summary_is_maintained_on_save_and_removed_on_delete()
    {
        var store = await SqliteAsync("maintain.db");
        var at = DateTimeOffset.UtcNow;
        await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(4), at);
        Assert.Equal(4, Assert.Single((await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10))).Items).CaptureCount);

        await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(6), at);
        var item = Assert.Single((await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10))).Items);
        Assert.Equal(6, item.CaptureCount);
        Assert.NotNull(item.FirstTimestamp);
        Assert.True(string.CompareOrdinal(item.FirstTimestamp, item.LastTimestamp) <= 0);

        while (await store.DeleteCapturesBatchAsync("a.com/", "html200", 100) > 0) { }
        await store.DeleteSearchAsync("a.com/", "html200");
        Assert.Empty((await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10))).Items);
    }

    [Fact]
    public async Task Backfill_creates_summary_for_existing_data_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-p5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "old.db");
        var store = new SqliteArchiveStore($"Data Source={db}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("old.com/", "html200", SampleData.Captures(5), DateTimeOffset.UtcNow);

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SearchSummary";   // as if the database predates the summary table
            cmd.ExecuteNonQuery();
        }
        Assert.Empty((await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10))).Items);

        await store.InitializeAsync();   // startup runs the backfill
        await store.InitializeAsync();   // idempotent
        Assert.Equal(5, Assert.Single((await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10))).Items).CaptureCount);
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Capture_page_returns_only_the_requested_page_with_filters(string kind)
    {
        var store = await CreateAsync(kind);
        await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(12), DateTimeOffset.UtcNow);
        var id = (await store.GetUrlListAsync(Q(null, UrlSorts.Captures, null, 0, 10))).Items[0].Id;

        var page2 = await store.GetSearchCapturesPageAsync(id, new UrlCaptureFilter(null, null, null, null), 5, 5);
        Assert.Equal(12, page2.Total);
        Assert.Equal(5, page2.Items.Count);

        var none = await store.GetSearchCapturesPageAsync(id, new UrlCaptureFilter("no/such", null, null, null), 0, 5);
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);
    }
}
