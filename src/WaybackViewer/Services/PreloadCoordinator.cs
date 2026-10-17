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
public sealed class PreloadCoordinator : IHostedService, IDisposable
{
    private sealed record PendingCapture(Capture Capture, string JobKey);
    private sealed class JobState(BackgroundOperation operation, int baseCompleted)
    {
        public BackgroundOperation Operation = operation;
        public int BaseCompleted = baseCompleted;
        public bool Paused;
        public bool Stopping;
    }

    private readonly IPageLoader loader;
    private readonly IArchiveStore store;
    private readonly PreloadOptions options;
    private readonly ILogger<PreloadCoordinator> log;

    private readonly object gate = new();
    private readonly LinkedList<PendingCapture> pending = new();
    private readonly Dictionary<string, LinkedListNode<PendingCapture>> pendingIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> inFlight = new(StringComparer.Ordinal);
    private readonly Channel<(NewPage Page, string JobKey)> saveQueue = Channel.CreateBounded<(NewPage, string)>(
        new BoundedChannelOptions(500) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cts = new();

    private int total, completed, failed, activeWorkers, unsaved;
    private long batchStartTicks, batchEndTicks;
    private bool disposed;
    private Task? writer;
    private TaskCompletionSource idle = NewIdle(true);
    private TaskCompletionSource resumeSignal = NewSignal(true);
    private readonly Dictionary<string, JobState> jobs = new(StringComparer.Ordinal);

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
        => Enqueue(captures, null, null);

    public int Enqueue(IEnumerable<StoredCapture> captures, string? prefix, string? filter)
    {
        var items = captures.ToList();
        var htmlItems = items.Where(item => item.Capture.IsHtml).ToList();
        var readyItems = htmlItems.Count(item => item.Ready);
        BackgroundOperation? requestedOperation = null;
        var jobKey = string.Empty;
        if (prefix is not null && filter is not null)
        {
            requestedOperation = store.CreateOrGetOperationAsync(prefix, filter, OperationKind.PagePreload,
                DateTimeOffset.UtcNow).GetAwaiter().GetResult();
            jobKey = requestedOperation.Key;
        }

        lock (gate)
        {
            if (disposed) return 0;
            if (requestedOperation is not null)
            {
                if (!jobs.TryGetValue(jobKey, out var job))
                {
                    job = new JobState(requestedOperation, readyItems);
                    jobs[jobKey] = job;
                }
                else
                {
                    job.Operation = requestedOperation;
                    job.BaseCompleted = Math.Max(job.BaseCompleted, readyItems);
                    job.Stopping = false;
                }
            }
            var wasBusy = IsBusyLocked();
            var added = 0;

            foreach (var item in items
                         .Where(c => !c.Ready && c.Capture.IsHtml)
                         .OrderBy(c => c.Capture.Timestamp, StringComparer.Ordinal))
            {
                var key = item.Capture.Key;
                if (pendingIndex.ContainsKey(key) || inFlight.ContainsKey(key)) continue;
                pendingIndex[key] = pending.AddLast(new PendingCapture(item.Capture, jobKey));
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

            if (requestedOperation is not null && jobs.TryGetValue(jobKey, out var requestedJob))
            {
                var resumedPaused = requestedJob.Operation.State == OperationState.Paused;
                requestedJob.Paused = resumedPaused;
                requestedJob.Operation = requestedJob.Operation with
                {
                    State = resumedPaused ? OperationState.Paused : OperationState.Running,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Total = Math.Max(requestedJob.Operation.Total, htmlItems.Count),
                    Completed = requestedJob.BaseCompleted + completed,
                    Failed = failed,
                    Error = null
                };
                PersistOperation(requestedJob.Operation);
            }

            writer ??= Task.Run(WriteLoopAsync);
            while (activeWorkers < options.Workers && activeWorkers < pending.Count)
            {
                activeWorkers++;
                _ = Task.Run(WorkerLoopAsync);
            }

            return added;
        }
    }

    public void Pause(string? prefix = null, string? filter = null)
    {
        lock (gate)
        {
            foreach (var job in MatchingJobsLocked(prefix, filter))
            {
                if (job.Paused || job.Stopping) continue;
                job.Paused = true;
                UpdateOperationLocked(job, OperationState.Paused);
            }
        }
    }

    public void Resume(string? prefix = null, string? filter = null)
    {
        lock (gate)
        {
            foreach (var job in MatchingJobsLocked(prefix, filter))
            {
                if (!job.Paused || job.Stopping) continue;
                job.Paused = false;
                UpdateOperationLocked(job, OperationState.Running);
            }
            resumeSignal.TrySetResult();
            resumeSignal = NewSignal(false);
        }
    }

    public void Stop(string? prefix = null, string? filter = null)
    {
        lock (gate)
        {
            foreach (var job in MatchingJobsLocked(prefix, filter))
            {
                job.Stopping = true;
                job.Paused = false;
                foreach (var item in pending.Where(item => item.JobKey == job.Operation.Key).ToList())
                {
                    if (!pendingIndex.Remove(item.Capture.Key, out var node)) continue;
                    pending.Remove(node);
                }
                UpdateOperationLocked(job, OperationState.Stopped);
            }
            resumeSignal.TrySetResult();
            CheckIdleLocked();
        }
    }

    public async Task<IReadOnlyList<BackgroundOperation>> GetOperationsAsync(CancellationToken ct = default) =>
        await store.GetOperationsAsync(activeOnly: true, ct).ConfigureAwait(false);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var operations = await store.GetOperationsAsync(activeOnly: false, cancellationToken).ConfigureAwait(false);
        foreach (var saved in operations.Where(o => o.Kind == OperationKind.PagePreload &&
                     o.State is OperationState.Running or OperationState.Queued or OperationState.Paused or OperationState.Interrupted))
        {
            var snapshot = await store.GetSearchAsync(saved.Prefix, saved.Filter, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null) Enqueue(snapshot.Captures, saved.Prefix, saved.Filter);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (IsBusyLocked())
                foreach (var job in jobs.Values.Where(job => !job.Stopping))
                    UpdateOperationLocked(job, OperationState.Interrupted);
        }
        return Task.CompletedTask;
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
            string jobKey;
            lock (gate)
            {
                if (disposed || pending.First is null)
                {
                    activeWorkers--;
                    CheckIdleLocked();
                    return;
                }
                var item = pending.First.Value;
                capture = item.Capture;
                jobKey = item.JobKey;
                pending.RemoveFirst();
                pendingIndex.Remove(capture.Key);
                inFlight[capture.Key] = jobKey;
            }

            await WaitIfPausedAsync(jobKey).ConfigureAwait(false);
            lock (gate)
            {
                if (jobs.TryGetValue(jobKey, out var job) && job.Stopping)
                {
                    inFlight.Remove(capture.Key);
                    CheckIdleLocked();
                    continue;
                }
            }

            NewPage page;
            try
            {
                page = await loader.LoadAsync(capture, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Preload failed for {Key}", capture.Key);
                MarkFailed(capture.Key, jobKey);
                continue;
            }

            lock (gate) unsaved++;
            try
            {
                await saveQueue.Writer.WriteAsync((page, jobKey), cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (gate) unsaved--;
                MarkFailed(capture.Key, jobKey);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = saveQueue.Reader;
        var batch = new List<(NewPage Page, string JobKey)>(options.WriteBatchSize);
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

    private async Task FlushAsync((NewPage Page, string JobKey)[] batch)
    {
        var ok = true;
        try
        {
            await store.SavePagesAsync(batch.Select(item => item.Page).ToArray(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ok = false;
            log.LogWarning(ex, "Saving a batch of {Count} pages failed", batch.Length);
        }

        lock (gate)
        {
            foreach (var item in batch) inFlight.Remove(item.Page.Capture.Key);
            completed += batch.Length;
            if (!ok) failed += batch.Length;
            unsaved -= batch.Length;
            foreach (var group in batch.GroupBy(item => item.JobKey, StringComparer.Ordinal))
            {
                if (!jobs.TryGetValue(group.Key, out var job)) continue;
                if (ok) UpdateOperationLocked(job, OperationState.Running, group.Count(), 0);
                else UpdateOperationLocked(job, OperationState.Running, 0, group.Count());
            }
            CheckIdleLocked();
        }
    }

    private void MarkFailed(string key, string jobKey)
    {
        lock (gate)
        {
            inFlight.Remove(key);
            failed++;
            completed++;
            if (jobs.TryGetValue(jobKey, out var job)) UpdateOperationLocked(job, OperationState.Running, 1, 1);
            CheckIdleLocked();
        }
    }

    private bool IsBusyLocked() => pending.Count > 0 || inFlight.Count > 0 || unsaved > 0;

    private void CheckIdleLocked()
    {
        if (IsBusyLocked()) return;
        if (batchEndTicks == 0) batchEndTicks = Stopwatch.GetTimestamp();
        foreach (var job in jobs.Values)
        {
            if (job.Stopping || job.Paused) continue;
            var finished = job.Operation.Completed + job.Operation.Failed >= job.Operation.Total && job.Operation.Total > 0;
            if (finished)
                UpdateOperationLocked(job, job.Operation.Failed > 0 ? OperationState.Failed : OperationState.Completed);
        }
        idle.TrySetResult();
    }

    private async Task WaitIfPausedAsync(string jobKey)
    {
        while (true)
        {
            Task wait;
            lock (gate)
                wait = jobs.TryGetValue(jobKey, out var job) && job.Paused ? resumeSignal.Task : Task.CompletedTask;
            await wait.ConfigureAwait(false);
            lock (gate)
            {
                if (!jobs.TryGetValue(jobKey, out var job) || !job.Paused) return;
            }
        }
    }

    private void UpdateOperationLocked(JobState job, OperationState state, int completedDelta = 0, int failedDelta = 0)
    {
        job.Operation = job.Operation with
        {
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            Completed = Math.Min(job.Operation.Total, job.Operation.Completed + completedDelta),
            Failed = job.Operation.Failed + failedDelta
        };
        PersistOperation(job.Operation);
    }

    private IEnumerable<JobState> MatchingJobsLocked(string? prefix, string? filter) =>
        jobs.Values.Where(job => prefix is null || filter is null ||
            (job.Operation.Prefix == prefix && job.Operation.Filter == filter));

    private void PersistOperation(BackgroundOperation value) => _ = store.UpdateOperationAsync(value);

    private static TaskCompletionSource NewIdle(bool completedAlready)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completedAlready) tcs.SetResult();
        return tcs;
    }

    private static TaskCompletionSource NewSignal(bool completedAlready)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completedAlready) tcs.SetResult();
        return tcs;
    }
}
