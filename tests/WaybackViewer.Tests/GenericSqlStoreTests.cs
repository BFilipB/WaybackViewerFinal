using WaybackViewer.Storage;
using WaybackViewer.Storage.Sql;

namespace WaybackViewer.Tests;

/// The database-independent store, run against SQLite through the SqliteDialect with the same scenarios every other
/// store must pass. If these pass, the generic logic is correct and only a dialect's SQL can still differ.
public class GenericSqlStoreOnSqliteTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wb-generic-" + Guid.NewGuid().ToString("N"));

    private SqlArchiveStore NewStore() =>
        new(new SqliteDialect(), $"Data Source={Path.Combine(dir, "nested", "g.db")}", new HtmlFileStore(Path.Combine(dir, "html")));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    [InlineData(nameof(StoreScenarios.MetadataRoundTrips))]
    [InlineData(nameof(StoreScenarios.LargeSearchRoundTrip))]
    [InlineData(nameof(StoreScenarios.PagedSearchRetrieval))]
    [InlineData(nameof(StoreScenarios.ConcurrentSavesDoNotCollide))]
    [InlineData(nameof(StoreScenarios.OutcomesRoundTripAndResetFailed))]
    public Task Scenario(string name) => StoreScenarios.Run(name, NewStore());

    [Fact]
    public void Every_scenario_is_covered_by_the_theory()
    {
        var covered = typeof(GenericSqlStoreOnSqliteTests).GetMethod(nameof(Scenario))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).First()[0]).ToHashSet();
        Assert.Equal(StoreScenarios.Names.OrderBy(n => n), covered.OrderBy(n => n));
    }
}

/// Runs the same scenarios against a real PostgreSQL. Skipped unless WAYBACK_TEST_POSTGRES holds a connection string
/// (the GitHub CI workflow starts a postgres service and sets it), so nothing needs installing locally.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYBACK_TEST_POSTGRES")))
            Skip = "Set WAYBACK_TEST_POSTGRES to a PostgreSQL connection string (runs in GitHub CI).";
    }
}

public class PostgresStoreTests
{
    private static SqlArchiveStore NewStore() =>
        new(new PostgresDialect(), Environment.GetEnvironmentVariable("WAYBACK_TEST_POSTGRES")!,
            new HtmlFileStore(Path.Combine(Path.GetTempPath(), "wb-pg-" + Guid.NewGuid().ToString("N"))));

    public static IEnumerable<object[]> Names => StoreScenarios.Names.Select(n => new object[] { n });

    [PostgresFact]
    public async Task All_shared_scenarios_pass_on_postgres()
    {
        foreach (var name in StoreScenarios.Names)
            await StoreScenarios.Run(name, NewStore());
    }

    [PostgresFact]
    public async Task Operations_and_search_text_work_on_postgres()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var tag = "pg" + Guid.NewGuid().ToString("N")[..8];
        var op = await store.CreateOrGetOperationAsync(tag, "html200", Models.OperationKind.PagePreload, DateTimeOffset.UtcNow);
        Assert.True(op.IsActive);
        Assert.Equal(op.Id, (await store.CreateOrGetOperationAsync(tag, "html200", Models.OperationKind.PagePreload, DateTimeOffset.UtcNow)).Id);
        Assert.Contains(await store.GetOperationsAsync(true), o => o.Id == op.Id);
        var found = await store.ListOperationSearchesAsync(new Models.OperationListQuery(null, null, 0, 10, tag.ToUpperInvariant()));
        Assert.Contains(found.Searches, s => s.Prefix == tag);              // LIKE is case-sensitive on PostgreSQL; the query lowercases

        var c = SampleData.Capture(1);
        await store.SavePagesAsync([new Models.NewPage(c, HtmlCodec.Compress("<p>zebra</p>"),
            new Models.ParsedPage("T", null, "zebra stripes"), DateTimeOffset.UtcNow)]);
        Assert.NotEmpty(await store.MatchCaptureIdsAsync("zebra"));
    }
}
