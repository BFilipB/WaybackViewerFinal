using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Background pipeline that downloads every capture that is not saved yet:
///   N download workers (oldest first = slideshow order)  ->  bounded channel  ->  ONE batching writer.
/// Pages near the slideshow cursor can be moved to the front with <see cref="Boost"/>.
public sealed class PreloadCoordinator : IDisposable
{
    private readonly IPageLoader loader;
    private readonly IArchiveStore store;
    private readonly PreloadOptions options;
    private readonly ILogger<PreloadCoordinator> log;

    private readonly object gate = new();
    private readonly LinkedList<Capture> pending = new();
    private readonly Dictionary<string, LinkedListNode<Capture>> pendingIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private readonly Channel<NewPage> saveQueue = Channel.CreateBounded<NewPage>(
        new BoundedChannelOptions(500) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cts = new();

    private int total, completed, failed, activeWorkers, unsaved;
    private long batchStartTicks, batchEndTicks;
    private bool disposed;
    private Task? writer;
    private TaskCompletionSource idle = NewIdle(true);

    public PreloadCoordinator(IPageLoader loader, IArchiveStore store, IOptions<PreloadOptions> options,
        ILogger<PreloadCoordinator> log)
    {
        this.loader = loader;
        this.store = store;
        this.options = options.Value;
        this.log = log;
    }

    /// Queues every HTML capture that is not saved yet. Returns how many were newly queued.
    public int Enqueue(IEnumerable<StoredCapture> captures)
    {
        lock (gate)
        {
            if (disposed) return 0;
            var wasBusy = IsBusyLocked();
            var added = 0;

            foreach (var item in captures
                         .Where(c => !c.Ready && c.Capture.IsHtml)
                         .OrderBy(c => c.Capture.Timestamp, StringComparer.Ordinal))
            {
                var key = item.Capture.Key;
                if (pendingIndex.ContainsKey(key) || inFlight.Contains(key)) continue;
                pendingIndex[key] = pending.AddLast(item.Capture);
                added++;
            }

            if (added == 0) return 0;
            if (!wasBusy)
            {
                total = completed = failed = 0;
                batchStartTicks = Stopwatch.GetTimestamp();
                batchEndTicks = 0;
                idle = NewIdle(false);
            }
            total += added;

            writer ??= Task.Run(WriteLoopAsync);
            while (activeWorkers < options.Workers && activeWorkers < pending.Count)
            {
                activeWorkers++;
                _ = Task.Run(WorkerLoopAsync);
            }
            return added;
        }
    }

    /// Moves the given captures (keys "timestamp|url") to the front of the queue, keeping their order.
    public void Boost(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        lock (gate)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!pendingIndex.TryGetValue(list[i], out var node)) continue;
                pending.Remove(node);
                pending.AddFirst(node);
            }
        }
    }

    /// A capture was downloaded some other way (on demand): drop it from the queue.
    public void MarkDone(string key)
    {
        lock (gate)
        {
            if (!pendingIndex.Remove(key, out var node)) return;
            pending.Remove(node);
            total--;
            CheckIdleLocked();
        }
    }

    public PreloadStatus GetStatus()
    {
        lock (gate)
        {
            var running = IsBusyLocked();
            double perSecond = 0;
            if (completed > 0 && batchStartTicks != 0)
            {
                var end = running || batchEndTicks == 0 ? Stopwatch.GetTimestamp() : batchEndTicks;
                var seconds = Stopwatch.GetElapsedTime(batchStartTicks, end).TotalSeconds;
                if (seconds > 0.25) perSecond = Math.Round(completed / seconds, 2);
            }
            return new PreloadStatus(total, completed, failed, running, perSecond);
        }
    }

    /// Completes when nothing is queued, downloading or waiting to be saved (used by tests).
    public Task WhenIdleAsync()
    {
        lock (gate) return idle.Task;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        cts.Cancel();
        saveQueue.Writer.TryComplete();
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            Capture capture;
            lock (gate)
            {
                if (disposed || pending.First is null)
                {
                    activeWorkers--;
                    CheckIdleLocked();
                    return;
                }
                capture = pending.First.Value;
                pending.RemoveFirst();
                pendingIndex.Remove(capture.Key);
                inFlight.Add(capture.Key);
            }

            NewPage page;
            try
            {
                page = await loader.LoadAsync(capture, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Preload failed for {Key}", capture.Key);
                MarkFailed(capture.Key);
                continue;
            }

            lock (gate) unsaved++;
            try
            {
                await saveQueue.Writer.WriteAsync(page, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (gate) unsaved--;
                MarkFailed(capture.Key);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = saveQueue.Reader;
        var batch = new List<NewPage>(options.WriteBatchSize);
        try
        {
            while (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                batch.Clear();
                var deadline = DateTime.UtcNow.AddMilliseconds(options.WriteMaxDelayMs);

                while (batch.Count < options.WriteBatchSize)
                {
                    if (reader.TryRead(out var item)) { batch.Add(item); continue; }

                    var remaining = deadline - DateTime.UtcNow;
                    if (batch.Count == 0 || remaining <= TimeSpan.Zero) break;

                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    wait.CancelAfter(remaining);
                    try { if (!await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break; }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested) { break; }
                }

                if (batch.Count > 0) await FlushAsync(batch.ToArray()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task FlushAsync(NewPage[] batch)
    {
        var ok = true;
        try
        {
            await store.SavePagesAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ok = false;
            log.LogWarning(ex, "Saving a batch of {Count} pages failed", batch.Length);
        }

        lock (gate)
        {
            foreach (var page in batch) inFlight.Remove(page.Capture.Key);
            completed += batch.Length;
            if (!ok) failed += batch.Length;
            unsaved -= batch.Length;
            CheckIdleLocked();
        }
    }

    private void MarkFailed(string key)
    {
        lock (gate)
        {
            inFlight.Remove(key);
            failed++;
            completed++;
            CheckIdleLocked();
        }
    }

    private bool IsBusyLocked() => pending.Count > 0 || inFlight.Count > 0 || unsaved > 0;

    private void CheckIdleLocked()
    {
        if (IsBusyLocked()) return;
        if (batchEndTicks == 0) batchEndTicks = Stopwatch.GetTimestamp();
        idle.TrySetResult();
    }

    private static TaskCompletionSource NewIdle(bool completedAlready)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completedAlready) tcs.SetResult();
        return tcs;
    }
}
