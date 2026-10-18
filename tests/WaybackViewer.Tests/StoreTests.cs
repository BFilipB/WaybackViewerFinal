using System.Reflection;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Behaviour every IArchiveStore must have. Run against the in-memory store and against a real
/// SQLite (a real database file in a temp folder).
public static class StoreScenarios
{
    public static readonly string[] Names =
    [
        nameof(InitializeIsIdempotent), nameof(UnknownSearchIsNull), nameof(SearchRoundTripsNewestFirst),
        nameof(ResavingReplacesMembershipButKeepsPages), nameof(FiltersAreIndependent), nameof(ReadyFlagFollowsSavedPages),
        nameof(PageWithAndWithoutHtml), nameof(UnknownPageIsNull), nameof(SavingAPageTwiceOverwrites),
        nameof(DuplicatesInOneSaveAreStoredOnce), nameof(SavingUnknownCaptureCreatesIt), nameof(UnicodeAndVeryLongUrls),
        nameof(LargeSearchRoundTrip), nameof(PagedSearchRetrieval), nameof(ConcurrentSavesDoNotCollide)
    ];

    private static string Unique() => "t" + Guid.NewGuid().ToString("N")[..12];

    private static NewPage Page(Capture c, string title = "T", string text = "body") =>
        new(c, HtmlCodec.Compress($"<title>{title}</title><p>{text}</p>"), new ParsedPage(title, "desc", text), DateTimeOffset.UtcNow);

    private static Capture Cap(string tag, int i, string mime = "text/html") =>
        new(new DateTime(2021, 1, 1).AddMinutes(i).ToString("yyyyMMddHHmmss"), $"https://{tag}.example/p{i}", mime, "200");

    public static async Task InitializeIsIdempotent(IArchiveStore store)
    {
        await store.InitializeAsync();
        await store.InitializeAsync();
    }

    public static async Task PagedSearchRetrieval(IArchiveStore store)
    {
        var tag = Unique();
        var captures = Enumerable.Range(0, 7).Select(i => Cap(tag, i)).ToList();
        await store.SaveSearchAsync(tag, "all", captures, DateTimeOffset.UtcNow);

        var page = await store.GetSearchPageAsync(tag, "all", 2, 3);
        Assert.NotNull(page);
        Assert.Equal(7, page!.TotalCount);
        Assert.Equal(captures.OrderByDescending(c => c.Timestamp).Skip(2).Take(3),
            page.Captures.Select(c => c.Capture));
        Assert.Empty((await store.GetSearchPageAsync(tag, "all", 20, 3))!.Captures);
    }

    public static async Task UnknownSearchIsNull(IArchiveStore store) =>
        Assert.Null(await store.GetSearchAsync(Unique(), "html200"));

    public static async Task SearchRoundTripsNewestFirst(IArchiveStore store)
    {
        var tag = Unique();
        var saved = new List<Capture> { Cap(tag, 2), Cap(tag, 9), Cap(tag, 1), Cap(tag, 5, "image/png") };
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var snapshot = await store.SaveSearchAsync(tag, "html200", saved, at);
        var loaded = await store.GetSearchAsync(tag, "html200");

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.SearchId, loaded!.SearchId);
        Assert.True(Math.Abs((loaded.CachedAt - at).TotalSeconds) < 1, "cached-at timestamp should round-trip");
        Assert.Equal(saved.OrderByDescending(c => c.Timestamp).ToList(), loaded.Captures.Select(c => c.Capture).ToList());
        Assert.All(loaded.Captures, c => Assert.False(c.Ready));
        Assert.Equal(4, loaded.Captures.Select(c => c.Id).Distinct().Count());
    }

    public static async Task ResavingReplacesMembershipButKeepsPages(IArchiveStore store)
    {
        var tag = Unique();
        var a = Cap(tag, 1); var b = Cap(tag, 2); var c = Cap(tag, 3);
        await store.SaveSearchAsync(tag, "html200", [a, b], DateTimeOffset.UtcNow);
        await store.SavePagesAsync([Page(a, "A")]);

        var second = await store.SaveSearchAsync(tag, "html200", [a, c], DateTimeOffset.UtcNow);

        Assert.Equal(new[] { c, a }, second.Captures.Select(x => x.Capture).ToArray());
        Assert.True(second.Captures.Single(x => x.Capture == a).Ready);
        Assert.False(second.Captures.Single(x => x.Capture == c).Ready);
        Assert.DoesNotContain(b, second.Captures.Select(x => x.Capture)); // dropped from this search
    }

    public static async Task FiltersAreIndependent(IArchiveStore store)
    {
        var tag = Unique();
        await store.SaveSearchAsync(tag, "html200", [Cap(tag, 1)], DateTimeOffset.UtcNow);
        await store.SaveSearchAsync(tag, "all", [Cap(tag, 1), Cap(tag, 2, "image/png")], DateTimeOffset.UtcNow);

        Assert.Single((await store.GetSearchAsync(tag, "html200"))!.Captures);
        Assert.Equal(2, (await store.GetSearchAsync(tag, "all"))!.Captures.Count);
    }

    public static async Task ReadyFlagFollowsSavedPages(IArchiveStore store)
    {
        var tag = Unique();
        var caps = Enumerable.Range(0, 6).Select(i => Cap(tag, i)).ToList();
        await store.SaveSearchAsync(tag, "html200", caps, DateTimeOffset.UtcNow);
        await store.SavePagesAsync(caps.Take(2).Select(c => Page(c)).ToList());

        var loaded = await store.GetSearchAsync(tag, "html200");
        Assert.Equal(2, loaded!.Captures.Count(c => c.Ready));
        Assert.All(loaded.Captures.Where(c => c.Ready), c => Assert.True(caps.Take(2).Contains(c.Capture)));
    }

    public static async Task PageWithAndWithoutHtml(IArchiveStore store)
    {
        var c = Cap(Unique(), 1);
        await store.SavePagesAsync([Page(c, "Title", "Some text")]);

        var full = await store.GetPageAsync(c.Timestamp, c.Original, includeHtml: true);
        var light = await store.GetPageAsync(c.Timestamp, c.Original, includeHtml: false);

        Assert.Equal(c, full!.Capture);
        Assert.Equal("<title>Title</title><p>Some text</p>", HtmlCodec.Decompress(full.HtmlGz!));
        Assert.Equal(new ParsedPage("Title", "desc", "Some text"), full.Parsed);
        Assert.Null(light!.HtmlGz);
        Assert.Equal(full.Parsed, light.Parsed);
    }

    public static async Task UnknownPageIsNull(IArchiveStore store) =>
        Assert.Null(await store.GetPageAsync("20200101000000", "https://" + Unique() + ".example/", true));

    public static async Task SavingAPageTwiceOverwrites(IArchiveStore store)
    {
        var c = Cap(Unique(), 1);
        await store.SavePagesAsync([Page(c, "old", "old text")]);
        await store.SavePagesAsync([Page(c, "new", "new text")]);

        var page = await store.GetPageAsync(c.Timestamp, c.Original, true);
        Assert.Equal("new", page!.Parsed.Title);
        Assert.Contains("new text", HtmlCodec.Decompress(page.HtmlGz!));
    }

    public static async Task DuplicatesInOneSaveAreStoredOnce(IArchiveStore store)
    {
        var tag = Unique();
        var c = Cap(tag, 1);
        await store.SavePagesAsync([Page(c, "first"), Page(c, "second")]);
        Assert.NotNull(await store.GetPageAsync(c.Timestamp, c.Original, false));

        var snapshot = await store.SaveSearchAsync(tag, "html200", [c, c, c], DateTimeOffset.UtcNow);
        Assert.Single(snapshot.Captures);
    }

    public static async Task SavingUnknownCaptureCreatesIt(IArchiveStore store)
    {
        var tag = Unique();
        var c = Cap(tag, 4);
        await store.SavePagesAsync([Page(c)]);

        var snapshot = await store.SaveSearchAsync(tag, "html200", [c], DateTimeOffset.UtcNow);
        Assert.True(snapshot.Captures.Single().Ready);
    }

    public static async Task UnicodeAndVeryLongUrls(IArchiveStore store)
    {
        var tag = Unique();
        var unicode = new Capture("20200101000000", $"https://{tag}.example/Здраво/世界/😀?q=a&b=c", "text/html", "200");
        var longUrl = new Capture("20200101000001", $"https://{tag}.example/" + new string('a', 3000), "text/html", "200");

        await store.SaveSearchAsync(tag, "html200", [unicode, longUrl], DateTimeOffset.UtcNow);
        await store.SavePagesAsync([Page(unicode, "Здраво 世界 😀", "текст 文本")]);

        var loaded = await store.GetSearchAsync(tag, "html200");
        Assert.Contains(unicode, loaded!.Captures.Select(c => c.Capture));
        Assert.Contains(longUrl, loaded.Captures.Select(c => c.Capture));
        var page = await store.GetPageAsync(unicode.Timestamp, unicode.Original, false);
        Assert.Equal("Здраво 世界 😀", page!.Parsed.Title);
        Assert.Equal("текст 文本", page.Parsed.Text);
    }

    public static async Task LargeSearchRoundTrip(IArchiveStore store)
    {
        var tag = Unique();
        var caps = Enumerable.Range(0, 5000).Select(i => Cap(tag, i)).ToList();
        var started = DateTime.UtcNow;

        await store.SaveSearchAsync(tag, "html200", caps, DateTimeOffset.UtcNow);
        var loaded = await store.GetSearchAsync(tag, "html200");

        Assert.Equal(5000, loaded!.Captures.Count);
        Assert.Equal(caps[^1], loaded.Captures[0].Capture);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    public static async Task ConcurrentSavesDoNotCollide(IArchiveStore store)
    {
        var tag = Unique();
        var caps = Enumerable.Range(0, 40).Select(i => Cap(tag, i)).ToList();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            // every worker saves overlapping pages and the same search at the same time
            await store.SavePagesAsync(caps.Skip(worker * 3).Take(20).Select(c => Page(c, "w" + worker)).ToList());
            await store.SaveSearchAsync(tag, "html200", caps, DateTimeOffset.UtcNow);
        })));

        var loaded = await store.GetSearchAsync(tag, "html200");
        Assert.Equal(40, loaded!.Captures.Count);
        Assert.Equal(40, loaded.Captures.Select(c => c.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 8).SelectMany(w => caps.Skip(w * 3).Take(20)).Distinct().Count(), loaded.Captures.Count(c => c.Ready));
    }

    public static async Task Run(string name, IArchiveStore store)
    {
        var method = typeof(StoreScenarios).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                     ?? throw new ArgumentException("Unknown scenario " + name);
        await store.InitializeAsync();
        await (Task)method.Invoke(null, [store])!;
    }
}

public class InMemoryArchiveStoreTests
{
    [Theory]
    [InlineData(nameof(StoreScenarios.InitializeIsIdempotent))]
    [InlineData(nameof(StoreScenarios.UnknownSearchIsNull))]
    [InlineData(nameof(StoreScenarios.SearchRoundTripsNewestFirst))]
    [InlineData(nameof(StoreScenarios.ResavingReplacesMembershipButKeepsPages))]
    [InlineData(nameof(StoreScenarios.FiltersAreIndependent))]
    [InlineData(nameof(StoreScenarios.ReadyFlagFollowsSavedPages))]
    [InlineData(nameof(StoreScenarios.PageWithAndWithoutHtml))]
    [InlineData(nameof(StoreScenarios.UnknownPageIsNull))]
    [InlineData(nameof(StoreScenarios.SavingAPageTwiceOverwrites))]
    [InlineData(nameof(StoreScenarios.DuplicatesInOneSaveAreStoredOnce))]
    [InlineData(nameof(StoreScenarios.SavingUnknownCaptureCreatesIt))]
    [InlineData(nameof(StoreScenarios.UnicodeAndVeryLongUrls))]
    [InlineData(nameof(StoreScenarios.LargeSearchRoundTrip))]
    [InlineData(nameof(StoreScenarios.PagedSearchRetrieval))]
    [InlineData(nameof(StoreScenarios.ConcurrentSavesDoNotCollide))]
    public Task Scenario(string name) => StoreScenarios.Run(name, new InMemoryArchiveStore());

    [Fact]
    public void Every_scenario_is_covered_by_the_theory() =>
        Assert.Equal(15, StoreScenarios.Names.Length);
}

public class ArchiveStoreContractTests
{
    [Fact]
    public async Task Core_operation_scenarios_work_in_memory_and_sqlite()
    {
        var operations = new Func<IArchiveStore, Task>[]
        {
            StoreScenarios.InitializeIsIdempotent,
            StoreScenarios.UnknownSearchIsNull,
            StoreScenarios.SearchRoundTripsNewestFirst,
            StoreScenarios.PagedSearchRetrieval,
            StoreScenarios.ConcurrentSavesDoNotCollide
        };

        foreach (var operation in operations)
            await operation(new InMemoryArchiveStore());

        var dir = Path.Combine(Path.GetTempPath(), "wb-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sqlite = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "contract.db")}");
            foreach (var operation in operations)
                await operation(sqlite);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class SqliteArchiveStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wb-sqlite-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(dir, "nested", "test.db");
    private SqliteArchiveStore NewStore() => new($"Data Source={DbPath}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release file handles (Windows)
        try { Directory.Delete(dir, true); } catch { }
    }

    [Theory]
    [InlineData(nameof(StoreScenarios.InitializeIsIdempotent))]
    [InlineData(nameof(StoreScenarios.UnknownSearchIsNull))]
    [InlineData(nameof(StoreScenarios.SearchRoundTripsNewestFirst))]
    [InlineData(nameof(StoreScenarios.ResavingReplacesMembershipButKeepsPages))]
    [InlineData(nameof(StoreScenarios.FiltersAreIndependent))]
    [InlineData(nameof(StoreScenarios.ReadyFlagFollowsSavedPages))]
    [InlineData(nameof(StoreScenarios.PageWithAndWithoutHtml))]
    [InlineData(nameof(StoreScenarios.UnknownPageIsNull))]
    [InlineData(nameof(StoreScenarios.SavingAPageTwiceOverwrites))]
    [InlineData(nameof(StoreScenarios.DuplicatesInOneSaveAreStoredOnce))]
    [InlineData(nameof(StoreScenarios.SavingUnknownCaptureCreatesIt))]
    [InlineData(nameof(StoreScenarios.UnicodeAndVeryLongUrls))]
    [InlineData(nameof(StoreScenarios.LargeSearchRoundTrip))]
     [InlineData(nameof(StoreScenarios.PagedSearchRetrieval))]
    [InlineData(nameof(StoreScenarios.ConcurrentSavesDoNotCollide))]
    public Task Scenario(string name) => StoreScenarios.Run(name, NewStore());

    [Fact]
    public async Task Creates_the_database_file_and_its_folders()
    {
        Assert.False(File.Exists(DbPath));
        await NewStore().InitializeAsync();
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public async Task Data_survives_a_restart()
    {
        var c = SampleData.Capture(1);
        var first = NewStore();
        await first.InitializeAsync();
        await first.SaveSearchAsync("example.com/", "html200", new[] { c }, DateTimeOffset.UtcNow);
        await first.SavePagesAsync(new[] { new NewPage(c, HtmlCodec.Compress("<p>kept</p>"), new ParsedPage("T", null, "kept"), DateTimeOffset.UtcNow) });

        var second = NewStore(); // a brand-new store object on the same file = an app restart
        await second.InitializeAsync();
        var snapshot = await second.GetSearchAsync("example.com/", "html200");
        var page = await second.GetPageAsync(c.Timestamp, c.Original, true);

        Assert.True(snapshot!.Captures.Single().Ready);
        Assert.Equal("<p>kept</p>", HtmlCodec.Decompress(page!.HtmlGz!));
    }

    [Fact]
    public async Task Saving_thousands_of_pages_in_one_batch_is_fast()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var pages = Enumerable.Range(0, 300).Select(i =>
        {
            var c = SampleData.Capture(i);
            var html = SampleData.Html("P" + i, new string('x', 20_000));
            return new NewPage(c, HtmlCodec.Compress(html), new ParsedPage("P" + i, "d", "x"), DateTimeOffset.UtcNow);
        }).ToList();

        var started = DateTime.UtcNow;
        await store.SavePagesAsync(pages);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "a 300-page batch should be written in well under 5 s");
    }
}
