using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Phase 1: every queued capture ends saved, skipped or failed; temporary failures are retried; progress survives restarts.
public class PreloadCompletionTests
{
    private const string Prefix = "example.com/";
    private const string Filter = "html200";

    private static PreloadCoordinator Create(IPageLoader loader, IArchiveStore store, int maxAttempts = 8, int workers = 8) =>
        new(loader, store, Options.Create(new PreloadOptions
        {
            Workers = workers, WriteBatchSize = 50, WriteMaxDelayMs = 10,
            MaxAttempts = maxAttempts, RetryBaseDelayMs = 1, RetryMaxDelayMs = 5
        }), NullLogger<PreloadCoordinator>.Instance);

    private static async Task<IReadOnlyList<StoredCapture>> SeedAsync(IArchiveStore store, int count)
    {
        var snapshot = await store.SaveSearchAsync(Prefix, Filter, SampleData.Captures(count), DateTimeOffset.UtcNow);
        return snapshot.Captures;
    }

    private static async Task<BackgroundOperation> FinishedOperationAsync(IArchiveStore store)
    {
        BackgroundOperation? op = null;
        await SampleData.Eventually(async () =>
        {
            op = (await store.GetOperationsAsync()).Single(o => o.Kind == OperationKind.PagePreload);
            return !op.IsActive;
        });
        return op!;
    }

    [Fact]
    public async Task Transient_rate_limit_burst_after_1000_downloads_still_saves_all_5000()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var calls = 0;
        loader.Hook = _ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n is > 1000 and <= 1500)
                throw new HttpRequestException("Archive request failed with HTTP 429.", null, HttpStatusCode.TooManyRequests);
            return Task.CompletedTask;
        };
        using var coordinator = Create(loader, store);

        await coordinator.EnqueueAsync(await SeedAsync(store, 5000), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, coordinator.GetStatus().Failed);
        Assert.Equal(5000, store.PageCount);
        var op = await FinishedOperationAsync(store);
        Assert.Equal(OperationState.Completed, op.State);
        Assert.Equal((5000, 5000, 0), (op.Total, op.Completed, op.Failed));
    }

    [Fact]
    public async Task Failed_download_is_not_also_counted_as_completed()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        loader.FailKeys[SampleData.Capture(3).Key] = 0;
        using var coordinator = Create(loader, store);

        await coordinator.EnqueueAsync(await SeedAsync(store, 10), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var op = await FinishedOperationAsync(store);
        Assert.Equal(1, op.Failed);
        Assert.Equal(9, op.Completed);
        Assert.Equal(10, op.Total);
    }

    [Fact]
    public async Task Rate_limited_capture_is_retried_until_it_succeeds()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var target = SampleData.Capture(2).Key;
        var failures = 0;
        loader.Hook = c => c.Key == target && Interlocked.Increment(ref failures) <= 3
            ? throw new ArchiveRequestException(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1))
            : Task.CompletedTask;
        using var coordinator = Create(loader, store);

        await coordinator.EnqueueAsync(await SeedAsync(store, 5), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(5, store.PageCount);
        Assert.Equal(4, loader.Started.Count(k => k == target));
        Assert.Equal(OperationState.Completed, (await FinishedOperationAsync(store)).State);
    }

    [Fact]
    public async Task Exhausted_retries_end_as_a_persisted_failure()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var target = SampleData.Capture(1).Key;
        loader.Hook = c => c.Key == target
            ? throw new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable)
            : Task.CompletedTask;
        using var coordinator = Create(loader, store, maxAttempts: 3);

        await coordinator.EnqueueAsync(await SeedAsync(store, 4), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, loader.Started.Count(k => k == target));
        var outcomes = await store.GetOutcomesAsync(Prefix, Filter);
        Assert.Equal(PreloadOutcome.Failed, outcomes[target].Outcome);
        Assert.Equal("http-503-retries-exhausted", outcomes[target].Reason);
        var op = await FinishedOperationAsync(store);
        Assert.Equal((OperationState.CompletedWithFailures, 3, 1), (op.State, op.Completed, op.Failed));
        Assert.False(op.IsActive);
        Assert.Equal("1 captures could not be downloaded.", op.Error);
    }

    [Fact]
    public async Task Not_found_and_non_html_are_skipped_not_failed_and_not_retried()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var missing = SampleData.Capture(0).Key;
        var binary = SampleData.Capture(1).Key;
        loader.Hook = c =>
            c.Key == missing ? throw new HttpRequestException("404", null, HttpStatusCode.NotFound) :
            c.Key == binary ? throw new InvalidDataException("binary") :
            Task.CompletedTask;
        using var coordinator = Create(loader, store);

        await coordinator.EnqueueAsync(await SeedAsync(store, 6), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(loader.Started, k => k == missing);
        var outcomes = await store.GetOutcomesAsync(Prefix, Filter);
        Assert.Equal(("not-found", PreloadOutcome.Skipped), (outcomes[missing].Reason, outcomes[missing].Outcome));
        Assert.Equal(("non-html", PreloadOutcome.Skipped), (outcomes[binary].Reason, outcomes[binary].Outcome));
        Assert.Equal(2, coordinator.GetStatus().Skipped);
        var op = await FinishedOperationAsync(store);
        Assert.Equal((OperationState.Completed, 4, 4, 0), (op.State, op.Total, op.Completed, op.Failed));
    }

    [Fact]
    public async Task Skipped_and_failed_captures_are_not_queued_again_but_retry_failed_requeues_them()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var bad = SampleData.Capture(1).Key;
        loader.FailKeys[bad] = 0;
        using var coordinator = Create(loader, store);
        await coordinator.EnqueueAsync(await SeedAsync(store, 3), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await FinishedOperationAsync(store);

        var again = (await store.GetSearchAsync(Prefix, Filter))!.Captures;
        Assert.Equal(0, await coordinator.EnqueueAsync(again, Prefix, Filter));

        loader.FailKeys.TryRemove(bad, out _);
        Assert.Equal(1, await coordinator.RetryFailedAsync(Prefix, Filter));
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, store.PageCount);
        Assert.Empty(await store.GetOutcomesAsync(Prefix, Filter));
        await SampleData.Eventually(async () =>
            (await store.GetOperationsAsync()).Single().State == OperationState.Completed);
    }

    [Fact]
    public async Task A_temporarily_failing_database_does_not_lose_downloaded_pages()
    {
        var store = new DelayedSaveStore { FailFirstSaves = 2 };
        store.SaveGate.SetResult();
        var loader = new FakePageLoader();
        using var coordinator = Create(loader, store);

        await coordinator.EnqueueAsync(await SeedAsync(store, 20), Prefix, Filter);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(store.SaveCalls > 2);
        Assert.Equal(0, coordinator.GetStatus().Failed);
        Assert.All((await store.GetSearchAsync(Prefix, Filter))!.Captures, c => Assert.True(c.Ready));
    }

    [Fact]
    public async Task Full_save_queue_applies_backpressure_instead_of_failing()
    {
        var store = new DelayedSaveStore();   // the writer blocks: the 500-slot save queue fills up
        var loader = new FakePageLoader();
        using var coordinator = Create(loader, store, workers: 16);

        await coordinator.EnqueueAsync(await SeedAsync(store, 1200), Prefix, Filter);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SampleData.Eventually(() => loader.Started.Count >= 500);
        await Task.Delay(100);
        Assert.True(loader.Started.Count < 1200, "workers must wait for the writer, not keep downloading");
        Assert.Equal(0, coordinator.GetStatus().Failed);

        store.SaveGate.SetResult();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, coordinator.GetStatus().Failed);
        Assert.All((await store.GetSearchAsync(Prefix, Filter))!.Captures, c => Assert.True(c.Ready));
    }

    [Fact]
    public async Task Interrupted_preload_resumes_after_restart_and_keeps_outcomes()
    {
        var store = new InMemoryArchiveStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var crash = new CancellationTokenSource();
        var first = new FakePageLoader();
        var skippedKey = SampleData.Capture(0).Key;
        first.Hook = c => c.Key == skippedKey
            ? throw new HttpRequestException("404", null, HttpStatusCode.NotFound)
            : c.Key == SampleData.Capture(1).Key ? Task.CompletedTask : gate.Task.WaitAsync(crash.Token);
        var coordinator = Create(first, store, workers: 2);
        await coordinator.EnqueueAsync(await SeedAsync(store, 30), Prefix, Filter);
        await SampleData.Eventually(async () => (await store.GetOutcomesAsync(Prefix, Filter)).Count == 1 && store.PageCount == 1);

        var stopping = coordinator.StopAsync(CancellationToken.None);   // app shuts down mid-run
        crash.Cancel();                                                  // in-flight downloads are torn down
        await stopping;
        coordinator.Dispose();
        await SampleData.Eventually(async () =>
            (await store.GetOperationsAsync()).Single().State == OperationState.Interrupted);

        var second = new FakePageLoader();
        using var restarted = Create(second, store);
        await restarted.StartAsync(CancellationToken.None);
        await restarted.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(skippedKey, second.Started);               // persisted skip is honoured
        Assert.DoesNotContain(SampleData.Capture(1).Key, second.Started); // saved page is not downloaded again
        Assert.Equal(29, store.PageCount);
        var op = await FinishedOperationAsync(store);
        Assert.Equal((OperationState.Completed, 29, 29), (op.State, op.Total, op.Completed));
    }
}
