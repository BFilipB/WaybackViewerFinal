using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// One operation state/progress change. Id is a monotonically increasing sequence used by SSE Last-Event-ID.
public sealed record OperationEvent(long Id, string Kind, string State, string Prefix, string Filter,
    int Completed, int Total, int Failed, bool Terminal);

/// Bounded in-memory event log with catch-up. Old events fall off the ring; clients that fall behind resync via polling.
public sealed class OperationEventBus
{
    private readonly object gate = new();
    private readonly Queue<OperationEvent> ring = new();
    private readonly int capacity;
    private long lastId;
    private TaskCompletionSource signal = NewSignal();

    public OperationEventBus(int capacity = 500) => this.capacity = Math.Max(1, capacity);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long LastId { get { lock (gate) return lastId; } }

    public OperationEvent Publish(BackgroundOperation op)
    {
        TaskCompletionSource toRelease;
        OperationEvent ev;
        lock (gate)
        {
            ev = new OperationEvent(++lastId, op.Kind.ToString(), op.State.ToString(), op.Prefix, op.Filter,
                op.Completed, op.Total, op.Failed, !op.IsActive);
            ring.Enqueue(ev);
            while (ring.Count > capacity) ring.Dequeue();
            toRelease = signal;
            signal = NewSignal();
        }
        toRelease.TrySetResult();
        return ev;
    }

    /// Events with Id greater than afterId (at most limit).
    public IReadOnlyList<OperationEvent> Since(long afterId, int limit = 200)
    {
        lock (gate) return ring.Where(e => e.Id > afterId).Take(Math.Max(1, limit)).ToList();
    }

    /// Completes when something newer than afterId exists, or throws on cancellation.
    public Task WaitAsync(long afterId, CancellationToken ct)
    {
        Task wait;
        lock (gate)
        {
            if (lastId > afterId) return Task.CompletedTask;
            wait = signal.Task;
        }
        return wait.WaitAsync(ct);
    }
}

/// Watches persisted operations and publishes a change event whenever state or progress changes.
/// Tracks only active operations, so memory stays bounded by the number of running operations.
public sealed class OperationEventPublisher(IArchiveStore store, OperationEventBus bus, ILogger<OperationEventPublisher> log)
    : BackgroundService
{
    private readonly Dictionary<long, BackgroundOperation> tracked = new();
    private readonly Dictionary<long, DateTime> lastSent = new();

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(750);

    /// Progress-only changes of one operation are published at most this often (state changes go out at once),
    /// so open browser tabs are not woken up several times a second while downloads run.
    public TimeSpan MinProgressInterval { get; init; } = TimeSpan.FromSeconds(1);

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Operation event publishing failed; will retry"); }
            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var active = await store.GetOperationsAsync(true, ct).ConfigureAwait(false);
        var seen = new HashSet<long>();
        foreach (var op in active)
        {
            seen.Add(op.Id);
            var now = Clock();
            if (tracked.TryGetValue(op.Id, out var prev) && !Changed(prev, op)) continue;
            if (prev is not null && prev.State == op.State && lastSent.TryGetValue(op.Id, out var at) && now - at < MinProgressInterval)
                continue;   // progress only, too soon: keep the last published snapshot so the change is sent next time
            bus.Publish(op);
            tracked[op.Id] = op;
            lastSent[op.Id] = now;
        }
        foreach (var id in tracked.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var prev = tracked[id];
            tracked.Remove(id);
            lastSent.Remove(id);
            var current = (await store.GetOperationsForSearchAsync(prev.Prefix, prev.Filter, ct).ConfigureAwait(false))
                .FirstOrDefault(o => o.Id == id);
            if (current is not null) bus.Publish(current);   // finished (terminal) state
        }
    }

    private static bool Changed(BackgroundOperation a, BackgroundOperation b) =>
        a.State != b.State || a.Completed != b.Completed || a.Total != b.Total || a.Failed != b.Failed;
}
