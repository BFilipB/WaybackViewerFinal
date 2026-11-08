using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Runs capture-list loads (CaptureLoad / WaybackRefresh) in the background, resumes the unfinished ones after a
/// restart, and queues a delta preload whenever a capture list is saved. Independent of PreloadCoordinator:
/// a capture-list load never waits for, pauses or cancels a preload and vice versa.
public sealed class CaptureLoadRunner : IHostedService
{
    private readonly CaptureCatalog catalog;
    private readonly PreloadCoordinator preload;
    private readonly IArchiveStore store;
    private readonly ILogger<CaptureLoadRunner> log;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly List<Task> running = new();

    public CaptureLoadRunner(CaptureCatalog catalog, PreloadCoordinator preload, IArchiveStore store,
        ILogger<CaptureLoadRunner> log)
    {
        this.catalog = catalog;
        this.preload = preload;
        this.store = store;
        this.log = log;
        catalog.Saved += OnSavedAsync;
    }

    /// New captures found by a load or refresh: queue only what is not saved/terminal yet (the delta).
    private async Task OnSavedAsync(string prefix, string filter, SearchSnapshot snapshot)
    {
        if (!snapshot.Captures.Any(c => c.Capture.IsHtml && !c.Ready)) return;
        await preload.EnqueueAsync(snapshot.Captures, prefix, filter, stopping.Token).ConfigureAwait(false);
    }

    /// Starts a load in the background. Concurrent identical loads share one download (CaptureCatalog).
    public void Start(string prefix, string filter, bool refresh)
    {
        lock (gate)
        {
            running.RemoveAll(t => t.IsCompleted);
            loading.Add($"{filter}|{prefix}");
            running.Add(Task.Run(() => RunAsync(prefix, filter, refresh)));
        }
    }

    private readonly HashSet<string> loading = new(StringComparer.Ordinal);

    /// True while a capture-list load of this searched URL is running in this process.
    public bool IsLoading(string prefix, string filter)
    {
        lock (gate) return loading.Contains($"{filter}|{prefix}");
    }

    /// Completes when no started load is running (used by tests).
    public Task WhenIdleAsync()
    {
        lock (gate) return Task.WhenAll(running.ToArray());
    }

    private async Task RunAsync(string prefix, string filter, bool refresh)
    {
        try { await RunCoreAsync(prefix, filter, refresh).ConfigureAwait(false); }
        finally { lock (gate) loading.Remove($"{filter}|{prefix}"); }
    }

    private async Task RunCoreAsync(string prefix, string filter, bool refresh)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "capture-load",
            ["prefix"] = prefix,
            ["filter"] = filter,
            ["refresh"] = refresh
        });
        try
        {
            var result = await catalog.GetAsync(prefix, filter, refresh, stopping.Token).ConfigureAwait(false);
            if (result.FromDatabase) await FinishCachedAsync(prefix, filter, refresh, result.Snapshot.Captures.Count).ConfigureAwait(false);
            // A cached list raises no Saved event, but may still have pages left to preload.
            if (result.FromDatabase && result.Snapshot.Captures.Any(c => c.Capture.IsHtml && !c.Ready))
                await preload.EnqueueAsync(result.Snapshot.Captures, prefix, filter, stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // shutting down: the operation stays unfinished and is resumed on the next start
        }
        catch (Exception ex)
        {
            // CaptureCatalog already marked the operation Failed with a client-safe message.
            log.LogWarning(ex, "Capture list load failed for {Prefix} ({Filter})", prefix, filter);
            var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
            var operation = await store.FindActiveOperationAsync(prefix, filter, kind, CancellationToken.None).ConfigureAwait(false);
            if (operation is not null && OperationStates.CanTransition(operation.State, OperationState.Failed))
                await store.UpdateOperationAsync(operation with
                {
                    State = OperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = CaptureCatalog.DescribeFailure(ex)
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// The list came from the database (fresh cache) so CaptureCatalog did not touch the operation: a resumed or
    /// newly queued one must still reach a terminal state.
    private async Task FinishCachedAsync(string prefix, string filter, bool refresh, int count)
    {
        var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
        var op = await store.FindActiveOperationAsync(prefix, filter, kind, stopping.Token).ConfigureAwait(false);
        if (op is null || OperationStates.Normalize(op.State) == OperationState.Paused) return;
        if (!OperationStates.CanTransition(op.State, OperationState.Completed)) return;
        await store.UpdateOperationAsync(op with
        {
            State = OperationState.Completed, UpdatedAt = DateTimeOffset.UtcNow, Total = count, Completed = count
        }, stopping.Token).ConfigureAwait(false);
    }

    /// Resumes capture-list loads that were queued/running/interrupted when the app stopped. Paused ones stay paused.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var active = await store.GetOperationsAsync(activeOnly: true, cancellationToken).ConfigureAwait(false);
        foreach (var op in active.Where(o => o.Kind is OperationKind.CaptureLoad or OperationKind.WaybackRefresh))
        {
            if (OperationStates.Normalize(op.State) == OperationState.Paused) continue;
            log.LogInformation("Resuming {Kind} for {Prefix} ({Filter})", op.Kind, op.Prefix, op.Filter);
            Start(op.Prefix, op.Filter, op.Kind == OperationKind.WaybackRefresh);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            log.LogInformation("Capture list loads still running at shutdown are resumed on the next start");
        }
    }
}
