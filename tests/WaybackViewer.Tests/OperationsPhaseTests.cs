using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class OperationStateMachineTests
{
    [Theory]
    [InlineData(OperationState.Queued, OperationState.Running, true)]
    [InlineData(OperationState.Running, OperationState.Paused, true)]
    [InlineData(OperationState.Paused, OperationState.Running, true)]
    [InlineData(OperationState.Running, OperationState.Completed, true)]
    [InlineData(OperationState.Running, OperationState.CompletedWithFailures, true)]
    [InlineData(OperationState.Interrupted, OperationState.Running, true)]
    [InlineData(OperationState.Completed, OperationState.Queued, true)]
    [InlineData(OperationState.Running, OperationState.Deleting, true)]
    [InlineData(OperationState.Completed, OperationState.Paused, false)]
    [InlineData(OperationState.Failed, OperationState.Paused, false)]
    [InlineData(OperationState.Deleting, OperationState.Running, false)]
    [InlineData(OperationState.Deleting, OperationState.Deleting, false)]
    [InlineData(OperationState.Stopped, OperationState.Running, true)]   // legacy Stopped behaves like Paused
    public void Transitions_are_validated(OperationState from, OperationState to, bool allowed) =>
        Assert.Equal(allowed, OperationStates.CanTransition(from, to));

    [Fact]
    public void Paused_and_interrupted_operations_are_active_and_terminal_ones_are_not()
    {
        Assert.True(OperationStates.IsActive(OperationState.Paused));
        Assert.True(OperationStates.IsActive(OperationState.Interrupted));
        Assert.False(OperationStates.IsActive(OperationState.Completed));
        Assert.False(OperationStates.IsActive(OperationState.CompletedWithFailures));
        Assert.True(OperationStates.IsActive(OperationState.Deleting));   // an unfinished delete resumes after a restart
    }

    [Fact]
    public async Task A_completed_preload_cannot_be_paused()
    {
        var store = new InMemoryArchiveStore();
        var coordinator = new PreloadCoordinator(new FakePageLoader(), store,
            Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10 }), NullLogger<PreloadCoordinator>.Instance);
        var snapshot = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(2), DateTimeOffset.UtcNow);
        await coordinator.EnqueueAsync(snapshot.Captures, "a.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await SampleData.Eventually(async () => (await store.GetOperationsAsync()).Single().State == OperationState.Completed);

        coordinator.Pause("a.com/", "html200");
        await Task.Delay(100);

        Assert.Equal(OperationState.Completed, (await store.GetOperationsAsync()).Single().State);
    }

    [Fact]
    public async Task Interrupted_operation_is_deduplicated_instead_of_recreated()
    {
        var store = new InMemoryArchiveStore();
        var first = await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(first with { State = OperationState.Interrupted, Completed = 5 });
        var again = await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(5, again.Completed);
    }
}

public class ScopedOperationQueryTests
{
    public static IEnumerable<object[]> Stores()
    {
        yield return ["memory"];
        yield return ["sqlite"];
    }

    private static async Task<(IArchiveStore Store, string? Dir)> CreateAsync(string kind)
    {
        if (kind == "memory") return (new InMemoryArchiveStore(), null);
        var dir = Path.Combine(Path.GetTempPath(), "wv-ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "ops.db")}");
        await store.InitializeAsync();
        return (store, dir);
    }

    private static void Cleanup(string? dir)
    {
        if (dir is null) return;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { /* temp folder; best effort */ }
    }

    private static async Task<BackgroundOperation> Op(IArchiveStore store, string prefix, OperationKind kind, OperationState state, int minutes)
    {
        var op = await store.CreateOrGetOperationAsync(prefix, "html200", kind, DateTimeOffset.UtcNow);
        var updated = op with { State = state, UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, minutes, 0, TimeSpan.Zero) };
        await store.UpdateOperationAsync(updated);
        return updated;
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Search_scoped_query_never_returns_other_urls(string kind)
    {
        var (store, dir) = await CreateAsync(kind);
        try
        {
            await Op(store, "a.com/", OperationKind.CaptureLoad, OperationState.Completed, 1);
            await Op(store, "a.com/", OperationKind.PagePreload, OperationState.Running, 2);
            await Op(store, "b.com/", OperationKind.PagePreload, OperationState.Running, 3);
            await Op(store, "c.com/", OperationKind.PagePreload, OperationState.Paused, 4);
            await Op(store, "d.com/", OperationKind.PagePreload, OperationState.Completed, 5);

            var ops = await ((IArchiveStore)store).GetOperationsForSearchAsync("a.com/", "html200");
            Assert.Equal(2, ops.Count);
            Assert.All(ops, o => Assert.Equal("a.com/", o.Prefix));
            Assert.Equal(2, await store.CountOtherActiveSearchesAsync("a.com/", "html200"));   // b and c; d is done
        }
        finally { Cleanup(dir); }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Searches_are_paginated_and_filtered(string kind)
    {
        var (store, dir) = await CreateAsync(kind);
        try
        {
            for (var i = 0; i < 7; i++)
                await Op(store, $"site{i}.com/", OperationKind.PagePreload, i % 2 == 0 ? OperationState.Running : OperationState.Completed, i);
            await Op(store, "site0.com/", OperationKind.CaptureLoad, OperationState.Completed, 0);

            var first = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 3));
            var third = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 6, 3));
            Assert.Equal(7, first.Total);                                      // one row per URL, not per operation
            Assert.Equal(["site6.com/", "site5.com/", "site4.com/"], first.Searches.Select(s => s.Prefix));
            Assert.Equal(["site0.com/"], third.Searches.Select(s => s.Prefix));

            var running = await store.ListOperationSearchesAsync(new OperationListQuery(null, [OperationState.Running], 0, 10));
            Assert.Equal(4, running.Total);

            var captureOnly = await store.ListOperationSearchesAsync(new OperationListQuery([OperationKind.CaptureLoad], null, 0, 10));
            Assert.Equal(["site0.com/"], captureOnly.Searches.Select(s => s.Prefix));

            var visible = await store.GetOperationsForSearchesAsync([new SearchKey("site0.com/", "html200"), new SearchKey("site1.com/", "html200")]);
            Assert.Equal(3, visible.Count);
        }
        finally { Cleanup(dir); }
    }

    [Theory, MemberData(nameof(Stores))]
    public async Task Searches_can_be_searched_and_sorted(string kind)
    {
        var (store, dir) = await CreateAsync(kind);
        try
        {
            await Op(store, "alpha.com/", OperationKind.PagePreload, OperationState.Completed, 1);
            await Op(store, "beta.com/", OperationKind.PagePreload, OperationState.Running, 2);
            await Op(store, "100%_off.com/", OperationKind.PagePreload, OperationState.Completed, 3);

            var hit = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 10, "ALPH"));
            Assert.Equal(["alpha.com/"], hit.Searches.Select(s => s.Prefix));
            Assert.Equal(1, hit.Total);

            var literal = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 10, "%_"));
            Assert.Equal(["100%_off.com/"], literal.Searches.Select(s => s.Prefix));

            var byUrl = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 10, null, OperationSort.Url, false));
            Assert.Equal(["100%_off.com/", "alpha.com/", "beta.com/"], byUrl.Searches.Select(s => s.Prefix));

            var oldest = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 10, null, OperationSort.Updated, false));
            Assert.Equal("alpha.com/", oldest.Searches[0].Prefix);

            var urgent = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 10, null, OperationSort.State, true));
            Assert.Equal("beta.com/", urgent.Searches[0].Prefix);                // Running sorts first
            var calm = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 10, null, OperationSort.State, false));
            Assert.Equal("beta.com/", calm.Searches[^1].Prefix);
        }
        finally { Cleanup(dir); }
    }
}

public class PauseAcrossRestartTests
{
    private static PreloadCoordinator Create(IArchiveStore store, FakePageLoader loader) =>
        new(loader, store, Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10 }), NullLogger<PreloadCoordinator>.Instance);

    [Fact]
    public async Task Paused_preload_stays_paused_after_restart_and_resumes_on_request()
    {
        var store = new InMemoryArchiveStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakePageLoader { Hook = _ => gate.Task };
        var coordinator = Create(store, first);
        var snapshot = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(5), DateTimeOffset.UtcNow);
        await coordinator.EnqueueAsync(snapshot.Captures, "a.com/", "html200");
        coordinator.Pause("a.com/", "html200");
        gate.SetResult();
        await SampleData.Eventually(async () => (await store.GetOperationsAsync()).Single().State == OperationState.Paused);
        await coordinator.StopAsync(CancellationToken.None);
        coordinator.Dispose();
        Assert.Equal(OperationState.Paused, (await store.GetOperationsAsync()).Single().State);

        var second = new FakePageLoader();
        using var restarted = Create(store, second);
        await restarted.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.Empty(second.Started);                                     // paused work is not downloaded
        Assert.Equal(OperationState.Paused, (await store.GetOperationsAsync()).Single().State);

        restarted.Resume("a.com/", "html200");
        await restarted.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await SampleData.Eventually(async () => (await store.GetOperationsAsync()).Single().State == OperationState.Completed);
        Assert.Equal(5, store.PageCount);
    }
}

public class SchedulerFairnessTests
{
    private static List<StoredCapture> Job(string site, int count) =>
        Enumerable.Range(1, count)
            .Select(i => new StoredCapture(i, new Capture($"2020010100{i:0000}", $"https://{site}/p{i}", "text/html", "200"), false))
            .ToList();

    [Fact]
    public async Task A_large_url_does_not_starve_small_ones()
    {
        var loader = new FakePageLoader();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Hook = _ => gate.Task;
        using var coordinator = new PreloadCoordinator(loader, new InMemoryArchiveStore(),
            Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10 }), NullLogger<PreloadCoordinator>.Instance);
        await coordinator.EnqueueAsync(Job("big.com", 500), "big.com/", "html200");
        await coordinator.EnqueueAsync(Job("s1.com", 3), "s1.com/", "html200");
        await coordinator.EnqueueAsync(Job("s2.com", 3), "s2.com/", "html200");
        gate.SetResult();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var order = loader.Started.ToArray();
        var lastSmall = Array.FindLastIndex(order, k => !k.Contains("big.com"));
        Assert.True(lastSmall < 12, $"small URLs finished at position {lastSmall}, expected within the first rounds");
    }

    [Fact]
    public async Task Priority_url_gets_a_larger_share_without_starving_others()
    {
        var loader = new FakePageLoader();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Hook = _ => gate.Task;
        using var coordinator = new PreloadCoordinator(loader, new InMemoryArchiveStore(),
            Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10 }), NullLogger<PreloadCoordinator>.Instance);
        await coordinator.EnqueueAsync(Job("a.com", 40), "a.com/", "html200");
        await coordinator.EnqueueAsync(Job("b.com", 40), "b.com/", "html200");
        await coordinator.EnqueueAsync(Job("c.com", 40), "c.com/", "html200");
        coordinator.SetPriority("c.com/", "html200");
        gate.SetResult();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        var first30 = loader.Started.Take(30).ToArray();
        var c = first30.Count(k => k.Contains("c.com"));
        Assert.True(c >= 15, $"priority URL got {c} of the first 30");
        Assert.Contains(first30, k => k.Contains("a.com"));
        Assert.Contains(first30, k => k.Contains("b.com"));
    }
}

public class CaptureLoadResumeTests
{
    [Fact]
    public async Task Unfinished_capture_load_resumes_on_startup_and_preloads_the_delta()
    {
        var archive = new FakeArchiveClient();
        archive.Route(ArchiveUrls.Cdx("a.com/", "html200"), SampleData.CdxJson(SampleData.Captures(4)));
        var store = new InMemoryArchiveStore();
        var op = await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(op with { State = OperationState.Running });   // the app died mid-load

        var catalog = new CaptureCatalog(store, archive, Options.Create(new StorageOptions()), new FakeTimeSource());
        var loader = new FakePageLoader();
        using var preload = new PreloadCoordinator(loader, store, Options.Create(new PreloadOptions { Workers = 2, WriteMaxDelayMs = 10 }),
            NullLogger<PreloadCoordinator>.Instance);
        var runner = new CaptureLoadRunner(catalog, preload, store, NullLogger<CaptureLoadRunner>.Instance);

        await runner.StartAsync(CancellationToken.None);
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await preload.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var ops = await ((IArchiveStore)store).GetOperationsForSearchAsync("a.com/", "html200");
        Assert.Equal(OperationState.Completed, ops.Single(o => o.Kind == OperationKind.CaptureLoad).State);
        Assert.True(store.PageCount == 4, string.Join(";", ops.Select(o => $"{o.Kind}:{o.State}:{o.Completed}/{o.Total}:{o.Error}")) + " started=" + loader.Started.Count);

        // refresh finds 2 new captures: only those are downloaded
        archive.Route(ArchiveUrls.Cdx("a.com/", "html200"), SampleData.CdxJson(SampleData.Captures(6)));
        loader.Started.Clear();
        runner.Start("a.com/", "html200", refresh: true);
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await preload.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, loader.Started.Count);
        Assert.Equal(6, store.PageCount);
        await runner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Paused_capture_load_is_not_resumed_on_startup()
    {
        var archive = new FakeArchiveClient();
        archive.Route(ArchiveUrls.Cdx("a.com/", "html200"), SampleData.CdxJson(SampleData.Captures(2)));
        var store = new InMemoryArchiveStore();
        var op = await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(op with { State = OperationState.Paused });
        var catalog = new CaptureCatalog(store, archive, Options.Create(new StorageOptions()), new FakeTimeSource());
        using var preload = new PreloadCoordinator(new FakePageLoader(), store, Options.Create(new PreloadOptions()),
            NullLogger<PreloadCoordinator>.Instance);
        var runner = new CaptureLoadRunner(catalog, preload, store, NullLogger<CaptureLoadRunner>.Instance);

        await runner.StartAsync(CancellationToken.None);
        await runner.WhenIdleAsync();

        Assert.Equal(0, archive.TotalCalls);
        Assert.Equal(OperationState.Paused, (await store.GetOperationAsync(op.Id))!.State);
    }
}

public class OperationsEndpointTests
{
    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task SeedAsync(AppHost host, int count)
    {
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        for (var i = 0; i < count; i++)
        {
            var op = await store.CreateOrGetOperationAsync($"site{i:00}.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
            await store.UpdateOperationAsync(op with { State = OperationState.Completed, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
        }
    }

    [Fact]
    public async Task Operations_page_renders_only_the_current_page_of_rows()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 30);

        var page1 = await host.GetAsync("/operations?pageSize=10");
        var page3 = await host.GetAsync("/operations?pageSize=10&page=3");

        Assert.Equal(10, CountRows(page1));
        Assert.Contains("site29.com/", page1);
        Assert.DoesNotContain("site05.com/", page1);
        Assert.Equal(10, CountRows(page3));
        Assert.Contains("site00.com/", page3);
        Assert.Contains("Page 3 of 3", page3);
    }

    [Fact]
    public async Task Operations_page_filters_by_state()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 3);
        var html = await host.GetAsync("/operations?state=active");
        Assert.Equal(0, CountRows(html));
        Assert.Contains("No operations match.", html);
    }

    [Fact]
    public async Task Bulk_pause_and_resume_apply_to_every_url()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var op = await store.CreateOrGetOperationAsync($"p{i}.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
            await store.UpdateOperationAsync(op with { State = OperationState.Queued });
            ids.Add(op.Id);
        }
        // nothing routed for these URLs: resume would fail the loads, so pause first and check states
        var pause = await host.Client.PostAsync("/operations/pause", Body(new { all = true }));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        foreach (var id in ids) Assert.Equal(OperationState.Paused, (await store.GetOperationAsync(id))!.State);

        host.RouteSite("p0.com/", SampleData.Captures(1));
        host.RouteSite("p1.com/", SampleData.Captures(1));
        host.RouteSite("p2.com/", SampleData.Captures(1));
        var resume = await host.Client.PostAsync("/operations/resume", Body(new { all = true }));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        await SampleData.Eventually(async () =>
        {
            foreach (var id in ids)
                if ((await store.GetOperationAsync(id))!.State != OperationState.Completed) return false;
            return true;
        });
    }

    [Fact]
    public async Task Selected_row_action_affects_only_that_url()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var a = await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
        var b = await store.CreateOrGetOperationAsync("b.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

        var response = await host.Client.PostAsync("/operations/pause", Body(new { keys = new[] { new { prefix = "a.com/", filter = "html200" } } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OperationState.Paused, (await store.GetOperationAsync(a.Id))!.State);
        Assert.Equal(OperationState.Queued, (await store.GetOperationAsync(b.Id))!.State);
    }

    [Fact]
    public async Task Status_endpoint_returns_only_the_requested_rows()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 5);
        var response = await host.Client.PostAsync("/operations/status",
            Body(new { keys = new[] { new { prefix = "site01.com/", filter = "html200" } } }));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToArray();
        Assert.Single(rows);
        Assert.Equal("site01.com/", rows[0].GetProperty("prefix").GetString());
    }

    [Fact]
    public async Task Operations_actions_reject_missing_antiforgery_token_and_bad_input()
    {
        await using var host = await AppHost.StartAsync();
        using var raw = new HttpClient { BaseAddress = host.Client.BaseAddress };
        var noToken = await raw.PostAsync("/operations/pause", Body(new { all = true }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var bad = await host.Client.PostAsync("/operations/pause", Body(new { keys = new[] { new { prefix = "has space", filter = "html200" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Home_workflow_status_never_returns_other_urls_and_counts_them()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
        var other = await store.CreateOrGetOperationAsync("b.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(other with { State = OperationState.Paused });

        using var wf = JsonDocument.Parse(await host.GetAsync("/Home/WorkflowStatus?url=a.com/&filter=html200"));
        Assert.Equal(1, wf.RootElement.GetProperty("otherActiveUrls").GetInt32());
        Assert.Single(wf.RootElement.GetProperty("timeline").EnumerateArray());

        using var ops = JsonDocument.Parse(await host.GetAsync("/Home/OperationStatus?url=a.com/&filter=html200"));
        Assert.All(ops.RootElement.EnumerateArray(), o => Assert.Equal("a.com/", o.GetProperty("prefix").GetString()));
    }

    private static int CountRows(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "<tr data-prefix=").Count;
}
