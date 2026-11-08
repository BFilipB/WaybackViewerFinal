using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Deletes the stored data of one searched URL as a persisted background operation (DeletePages / DeleteAll, state
/// Deleting). Order: remove the URL's preload job, pause its capture-list work, delete in small batches (rows first,
/// then the HTML files of the same batch), then remove the operation rows. Captures that another searched URL still
/// lists are never removed. An unfinished delete resumes after a restart.
public sealed class DeleteRunner : IHostedService
{
    public const int BatchSize = 500;

    private readonly IArchiveStore store;
    private readonly PreloadCoordinator preload;
    private readonly CaptureLoadRunner loads;
    private readonly ILogger<DeleteRunner> log;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly List<Task> running = new();
    private readonly HashSet<string> deleting = new(StringComparer.Ordinal);

    public DeleteRunner(IArchiveStore store, PreloadCoordinator preload, CaptureLoadRunner loads, ILogger<DeleteRunner> log)
    {
        this.store = store;
        this.preload = preload;
        this.loads = loads;
        this.log = log;
    }

    public Task<DeletePreview> PreviewAsync(string prefix, string filter, CancellationToken ct = default) =>
        store.GetDeletePreviewAsync(prefix, filter, ct);

    /// Starts a delete in the background. Refuses while a capture-list load of the URL is running (it would write
    /// the list again after the delete) and while another delete of the URL is unfinished.
    public async Task<DeleteStartResult> BeginAsync(string prefix, string filter, DeleteLevel level, CancellationToken ct = default)
    {
        var key = $"{filter}|{prefix}";
        if (loads.IsLoading(prefix, filter)) return DeleteStartResult.Busy;
        lock (gate)
        {
            if (!deleting.Add(key)) return DeleteStartResult.AlreadyDeleting;
        }

        try
        {
            foreach (var other in new[] { OperationKind.DeletePages, OperationKind.DeleteAll })
                if (await store.FindActiveOperationAsync(prefix, filter, other, ct).ConfigureAwait(false) is not null)
                {
                    lock (gate) deleting.Remove(key);
                    return DeleteStartResult.AlreadyDeleting;
                }

            var kind = level == DeleteLevel.All ? OperationKind.DeleteAll : OperationKind.DeletePages;
            var op = await store.CreateOrGetOperationAsync(prefix, filter, kind, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            op = op with { State = OperationState.Deleting, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
            await store.UpdateOperationAsync(op, ct).ConfigureAwait(false);
            Launch(op);
            return DeleteStartResult.Started;
        }
        catch
        {
            lock (gate) deleting.Remove(key);
            throw;
        }
    }

    private void Launch(BackgroundOperation op)
    {
        lock (gate)
        {
            deleting.Add($"{op.Filter}|{op.Prefix}");
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => RunAsync(op)));
        }
    }

    /// Completes when no started delete is running (used by tests).
    public Task WhenIdleAsync()
    {
        lock (gate) return Task.WhenAll(running.ToArray());
    }

    private async Task RunAsync(BackgroundOperation op)
    {
        var prefix = op.Prefix;
        var filter = op.Filter;
        var all = op.Kind == OperationKind.DeleteAll;
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "delete",
            ["prefix"] = prefix,
            ["filter"] = filter,
            ["level"] = all ? "all" : "pages"
        });
        var ct = stopping.Token;
        try
        {
            // 1) stop the URL's own work first: the preload job leaves the coordinator; capture-list operations are paused
            await preload.RemoveJobAsync(prefix, filter, ct).ConfigureAwait(false);
            if (all)
                foreach (var other in await store.GetOperationsForSearchAsync(prefix, filter, ct).ConfigureAwait(false))
                {
                    if (other.Kind is OperationKind.DeletePages or OperationKind.DeleteAll or OperationKind.PagePreload || !other.IsActive) continue;
                    if (OperationStates.CanTransition(other.State, OperationState.Paused))
                        await store.UpdateOperationAsync(other with { State = OperationState.Paused, UpdatedAt = DateTimeOffset.UtcNow }, ct)
                            .ConfigureAwait(false);
                }

            // 2) delete in batches; progress is persisted after every batch
            var preview = await store.GetDeletePreviewAsync(prefix, filter, ct).ConfigureAwait(false);
            var total = Math.Max(op.Total, all ? preview.Captures : preview.Pages);
            var done = op.Completed;
            op = await ReportAsync(op, total, done, ct).ConfigureAwait(false);

            while (true)
            {
                var removed = await store.DeletePagesBatchAsync(prefix, filter, BatchSize, ct).ConfigureAwait(false);
                if (removed == 0) break;
                if (!all) done += removed;
                op = await ReportAsync(op, total, done, ct).ConfigureAwait(false);
            }

            if (all)
            {
                while (true)
                {
                    var processed = await store.DeleteCapturesBatchAsync(prefix, filter, BatchSize, ct).ConfigureAwait(false);
                    if (processed == 0) break;
                    done += processed;
                    op = await ReportAsync(op, total, done, ct).ConfigureAwait(false);
                }
                await store.DeleteSearchAsync(prefix, filter, ct).ConfigureAwait(false);
            }

            // 3) remove the operation rows; for DeletePages the capture list and its capture-list operations stay
            IReadOnlyCollection<OperationKind> kinds = all
                ? [OperationKind.CaptureLoad, OperationKind.WaybackRefresh, OperationKind.PagePreload, OperationKind.DeletePages, OperationKind.DeleteAll]
                : [OperationKind.PagePreload, OperationKind.DeletePages];
            await store.DeleteOperationsAsync(prefix, filter, kinds, ct).ConfigureAwait(false);
            log.LogInformation("Deleted {Level} data of {Prefix} ({Filter})", all ? "all" : "page", prefix, filter);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // shutting down: the operation stays Deleting and is resumed on the next start
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Delete failed for {Prefix} ({Filter})", prefix, filter);
            await RecordFailureAsync(op).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) deleting.Remove($"{filter}|{prefix}");
            preload.ReleaseJob(prefix, filter);
        }
    }

    private async Task RecordFailureAsync(BackgroundOperation op)
    {
        try
        {
            // Deleting has no outgoing transitions, so the failure is written directly as the operation's last state.
            await store.UpdateOperationAsync(op with
            {
                State = OperationState.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = "Deleting failed. Try again; the server log has the details."
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            log.LogError(inner, "Could not record the failed delete of {Prefix} ({Filter})", op.Prefix, op.Filter);
        }
    }

    private async Task<BackgroundOperation> ReportAsync(BackgroundOperation op, int total, int done, CancellationToken ct)
    {
        var next = op with { Total = total, Completed = Math.Min(total, done), UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateOperationAsync(next, ct).ConfigureAwait(false);
        return next;
    }

    /// Resumes deletes that were unfinished when the app stopped.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var active = await store.GetOperationsAsync(activeOnly: true, cancellationToken).ConfigureAwait(false);
        foreach (var op in active.Where(o => o.Kind is OperationKind.DeletePages or OperationKind.DeleteAll))
        {
            log.LogInformation("Resuming {Kind} for {Prefix} ({Filter})", op.Kind, op.Prefix, op.Filter);
            Launch(op);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            log.LogInformation("Deletes still running at shutdown are resumed on the next start");
        }
    }
}
