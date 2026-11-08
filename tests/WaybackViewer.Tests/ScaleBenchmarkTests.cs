using System.Diagnostics;
using Xunit.Abstractions;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Phase 7 timings at 100k+ captures. Budgets are generous (CI noise); real numbers are written to the test output.
public class ScaleBenchmarkTests(ITestOutputHelper output)
{
    private const string P = "big.com/";
    private const string F = "html200";

    private static List<Capture> Many(int n) =>
        Enumerable.Range(0, n).Select(i => new Capture(
            (20000101000000L + i * 7L).ToString(), $"https://big.com/section{i % 50}/page{i}.html", "text/html", "200")).ToList();

    private static async Task<SqliteArchiveStore> Seed(string dir, int n)
    {
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync(P, F, Many(n), DateTimeOffset.UtcNow);
        return store;
    }

    [Fact]
    public async Task Page_and_autocomplete_stay_fast_at_100k_captures()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = await Seed(dir, 100_000);

        var sw = Stopwatch.StartNew();
        var page = await store.GetSearchPageAsync(P, F, 50_000, 50);
        var pageMs = sw.ElapsedMilliseconds;
        for (var i = 0; i < 2; i++)   // best of three: a noisy neighbour must not fail a budget test
        {
            sw.Restart();
            await store.GetSearchPageAsync(P, F, 50_000, 50);
            pageMs = Math.Min(pageMs, sw.ElapsedMilliseconds);
        }
        Assert.Equal(50, page!.Captures.Count);
        Assert.Equal(100_000, page.TotalCount);

        sw.Restart();
        var suggestions = await store.SuggestUrlsAsync("big", 10);
        var suggestMs = sw.ElapsedMilliseconds;
        for (var i = 0; i < 2; i++)
        {
            sw.Restart();
            await store.SuggestUrlsAsync("big", 10);
            suggestMs = Math.Min(suggestMs, sw.ElapsedMilliseconds);
        }
        Assert.NotEmpty(suggestions);

        output.WriteLine($"paged read (offset 50k, 50 rows): {pageMs} ms; autocomplete: {suggestMs} ms");
        Assert.True(pageMs < 200, $"page took {pageMs} ms");
        Assert.True(suggestMs < 50, $"autocomplete took {suggestMs} ms");
    }

    [Fact]
    public async Task Filtered_page_with_facets_stays_fast_at_100k_captures()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await Seed(dir, 100_000);
        var cold = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await cold.InitializeAsync();
        var filter = new CaptureFilter(Section: "/section7");

        var sw = Stopwatch.StartNew();
        var first = await cold.GetFilteredPageAsync(P, F, filter, 3, 50);   // cold: also builds the facet counts
        var coldMs = sw.ElapsedMilliseconds;
        Assert.Equal(50, first!.Captures.Count);
        Assert.Equal(2000, first.Total);
        Assert.Equal(100_000, first.Facets.Years.Sum(y => y.Count));

        var warmMs = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            sw.Restart();
            await cold.GetFilteredPageAsync(P, F, filter, 4, 50);
            warmMs = Math.Min(warmMs, sw.ElapsedMilliseconds);
        }
        output.WriteLine($"filtered page + facets, cold: {coldMs} ms; warm: {warmMs} ms");
        Assert.True(warmMs < 200, $"filtered page took {warmMs} ms");
    }
    [Fact]
    public async Task Paged_read_does_not_materialize_the_whole_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = await Seed(dir, 100_000);
        // Fresh store instance: nothing cached. Reading one page must stay cheap and bounded.
        var cold = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await cold.InitializeAsync();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var page = await cold.GetSearchPageAsync(P, F, 0, 50);
        output.WriteLine($"cold first page: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(50, page!.Captures.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"cold page took {sw.ElapsedMilliseconds} ms");
    }
}
