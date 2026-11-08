using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Pause / Resume / Retry failed / Refresh for whole searched URLs (both of their operations).
/// Used by the Operations page row and bulk actions. Every change goes through the state machine.
public sealed class OperationControl(IArchiveStore store, PreloadCoordinator preload, CaptureLoadRunner loads,
    ILogger<OperationControl> log)
{
    public async Task PauseAsync(SearchKey search, CancellationToken ct = default)
    {
        foreach (var op in await store.GetOperationsForSearchAsync(search.Prefix, search.Filter, ct).ConfigureAwait(false))
        {
            if (!op.IsActive || OperationStates.Normalize(op.State) == OperationState.Paused) continue;
            if (op.Kind == OperationKind.PagePreload && preload.HasJob(op.Prefix, op.Filter))
                preload.Pause(op.Prefix, op.Filter);
            else
                await SetAsync(op, OperationState.Paused, ct).ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync(SearchKey search, CancellationToken ct = default)
    {
        foreach (var op in await store.GetOperationsForSearchAsync(search.Prefix, search.Filter, ct).ConfigureAwait(false))
        {
            if (OperationStates.Normalize(op.State) is not (OperationState.Paused or OperationState.Interrupted)) continue;
            if (op.Kind == OperationKind.PagePreload)
            {
                if (preload.HasJob(op.Prefix, op.Filter)) { preload.Resume(op.Prefix, op.Filter); continue; }
                await SetAsync(op, OperationState.Running, ct).ConfigureAwait(false);
                var snapshot = await store.GetSearchAsync(op.Prefix, op.Filter, ct).ConfigureAwait(false);
                if (snapshot is not null)
                    await preload.EnqueueAsync(snapshot.Captures, op.Prefix, op.Filter, ct).ConfigureAwait(false);
            }
            else if (await SetAsync(op, OperationState.Queued, ct).ConfigureAwait(false))
            {
                loads.Start(op.Prefix, op.Filter, op.Kind == OperationKind.WaybackRefresh);
            }
        }
    }

    public Task<int> RetryFailedAsync(SearchKey search, CancellationToken ct = default) =>
        preload.RetryFailedAsync(search.Prefix, search.Filter, ct);

    /// Downloads the capture list again (a WaybackRefresh); new captures are then preloaded as a delta.
    public async Task RefreshAsync(SearchKey search, CancellationToken ct = default)
    {
        await store.CreateOrGetOperationAsync(search.Prefix, search.Filter, OperationKind.WaybackRefresh,
            DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        loads.Start(search.Prefix, search.Filter, refresh: true);
    }

    private async Task<bool> SetAsync(BackgroundOperation op, OperationState state, CancellationToken ct)
    {
        if (!OperationStates.CanTransition(op.State, state))
        {
            log.LogInformation("Ignored {State} for operation {Id} in state {Current}", state, op.Id, op.State);
            return false;
        }
        await store.UpdateOperationAsync(op with { State = state, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        return true;
    }
}
