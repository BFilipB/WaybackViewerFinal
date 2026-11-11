using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// The preload queue holds a bounded window per job; the database holds the rest and is read keyset-paged.
public class BoundedPreloadWindowTests
{
    private const string Prefix = "example.com/";
    private const string Filter = "all";

    private static async Task<SqliteArchiveStore> MakeStoreAsync(int captures)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "w.db")}", new HtmlFileStore(Path.Combine(dir, "h")));
        await store.InitializeAsync();
        await store.SaveSearchAsync(Prefix, Filter, SampleData.Captures(captures), DateTimeOffset.UtcNow);
        return store;
    }

    [Fact]
    public async Task Unsaved_batch_excludes_skipped_and_failed_captures_in_sql()
    {
        var store = await MakeStoreAsync(6);
        await store.SaveOutcomesAsync([
            new CaptureOutcome(SampleData.Capture(0), PreloadOutcome.Skipped, "not-found", DateTimeOffset.UtcNow),
            new CaptureOutcome(SampleData.Capture(1), PreloadOutcome.Failed, "http-403", DateTimeOffset.UtcNow)]);

        var batch = await store.GetUnsavedHtmlBatchAsync(Prefix, Filter, new CaptureFilter(), null, 100);

        Assert.Equal(Enumerable.Range(2, 4).Select(i => SampleData.Capture(i).Key), batch.Select(b => b.Capture.Key));
        Assert.Equal(new PreloadCounts(6, 6, 1, 1), await store.GetPreloadCountsAsync(Prefix, Filter));
    }

    [Fact]
    public async Task Large_search_is_queued_in_bounded_windows_and_fully_preloaded()
    {
        var store = await MakeStoreAsync(250);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new FakePageLoader { Hook = _ => gate.Task };
        using var coordinator = new PreloadCoordinator(loader, store,
            Options.Create(new PreloadOptions { Workers = 2, WriteMaxDelayMs = 10, MaxQueuedPerJob = 20 }),
            NullLogger<PreloadCoordinator>.Instance);

        await coordinator.EnqueueFilteredAsync(Prefix, Filter, new CaptureFilter(), 250, 250, 250, boost: false);
        await SampleData.Eventually(() => coordinator.GetStatus().InFlight == 2);
        Assert.True(coordinator.GetStatus().Queued <= 20, "only one window may be held in memory");

        gate.SetResult();
        await SampleData.Eventually(async () =>
            (await store.GetOperationsAsync()).Single().State == OperationState.Completed, 30_000);
        Assert.Equal(new PreloadCounts(250, 0, 0, 0), await store.GetPreloadCountsAsync(Prefix, Filter));
        Assert.Equal(250, loader.Started.Distinct().Count());
    }

    [Fact]
    public async Task Restart_resumes_from_the_database_without_loading_the_whole_list()
    {
        var store = await MakeStoreAsync(60);
        var html = SampleData.Html("t", "b");
        await store.SavePagesAsync(SampleData.Captures(10)
            .Select(c => new NewPage(c, HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow)).ToList());
        var op = await store.CreateOrGetOperationAsync(Prefix, Filter, OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(op with { State = OperationState.Interrupted });

        var loader = new FakePageLoader();
        using var coordinator = new PreloadCoordinator(loader, store,
            Options.Create(new PreloadOptions { Workers = 2, WriteMaxDelayMs = 10, MaxQueuedPerJob = 8 }),
            NullLogger<PreloadCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);
        await SampleData.Eventually(async () =>
            (await store.GetOperationsAsync()).Single().State == OperationState.Completed, 30_000);

        Assert.Equal(50, loader.Started.Distinct().Count());   // saved pages are not downloaded again
        var finished = (await store.GetOperationsAsync()).Single();
        Assert.Equal((60, 60), (finished.Total, finished.Completed));
    }

    [Fact]
    public async Task GetPreloadCountsAsync_CancelledToken_Throws()
    {
        var store = await MakeStoreAsync(5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPreloadCountsAsync(Prefix, Filter, cts.Token));
    }
}