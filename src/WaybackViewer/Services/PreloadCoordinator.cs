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
    private readonly Dictionary<string, LinkedList<PendingCapture>> queues = new(StringComparer.Ordinal);
    private readonly List<string> ring = new();
    private int ringCursor;
    private readonly Dictionary<string, LinkedListNode<PendingCapture>> pendingIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> inFlight = new(StringComparer.Ordinal);
    private readonly Channel<(NewPage Page, string JobKey)> saveQueue = Channel.CreateBounded<(NewPage, string)>(
        new BoundedChannelOptions(500) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cts = new();
    private readonly List<Task> workerTasks = new();

    private int total, completed, failed, activeWorkers, unsaved;
    private string? lastJobKey;
    private Task? opWriter;
    private readonly Channel<BackgroundOperation> opQueue = Channel.CreateUnbounded<BackgroundOperation>(
        new UnboundedChannelOptions { SingleReader = true });
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
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "preload",
            ["prefix"] = prefix,
            ["filter"] = filter
        });

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
                AddPendingLocked(new PendingCapture(item.Capture, jobKey));
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
            EnsureWorkersLocked();

            return added;
        }
    }

    public void Pause(string? prefix = null, string? filter = null)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "preload",
            ["action"] = "pause",
            ["prefix"] = prefix,
            ["filter"] = filter
        });
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
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "preload",
            ["action"] = "resume",
            ["prefix"] = prefix,
            ["filter"] = filter
        });
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
            EnsureWorkersLocked();
        }
    }

    public void Stop(string? prefix = null, string? filter = null)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "preload",
            ["action"] = "stop",
            ["prefix"] = prefix,
            ["filter"] = filter
        });
        lock (gate)
        {
            foreach (var job in MatchingJobsLocked(prefix, filter))
            {
                job.Stopping = true;
                job.Paused = false;
                ClearJobQueueLocked(job.Operation.Key);
                UpdateOperationLocked(job, OperationState.Stopped);
            }
            resumeSignal.TrySetResult();
            CheckIdleLocked();
        }
    }

    public void Pause(long operationId) => SetOperationState(operationId, OperationState.Paused);

    public void Resume(long operationId) => SetOperationState(operationId, OperationState.Running);

    public void Stop(long operationId)
    {
        lock (gate)
        {
            var job = jobs.Values.FirstOrDefault(j => j.Operation.Id == operationId);
            if (job is null) return;
            job.Stopping = true;
            job.Paused = false;
            ClearJobQueueLocked(job.Operation.Key);
            UpdateOperationLocked(job, OperationState.Stopped);
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] workers;
        Task? saveWriter;
        lock (gate)
        {
            disposed = true;
            if (IsBusyLocked())
                foreach (var job in jobs.Values.Where(job => !job.Stopping))
                    UpdateOperationLocked(job, OperationState.Interrupted);
            resumeSignal.TrySetResult();
            workers = workerTasks.Where(t => !t.IsCompleted).ToArray();
            saveWriter = writer;
        }

        // 1) wait for current download workers to stop producing new pages
        await WaitWithTimeoutAsync(Task.WhenAll(workers), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        // 2) complete save queue and let single writer flush all already-downloaded pages
        saveQueue.Writer.TryComplete();
        if (saveWriter is not null)
            await WaitWithTimeoutAsync(saveWriter, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        // 3) flush operation-state writes in order
        opQueue.Writer.TryComplete();
        if (opWriter is not null)
            await WaitWithTimeoutAsync(opWriter, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
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
                var owner = node.List!;
                owner.Remove(node);
                owner.AddFirst(node);
            }
        }
    }

    /// A capture was downloaded some other way (on demand): drop it from the queue.
    public void MarkDone(string key)
    {
        lock (gate)
        {
            if (!pendingIndex.TryGetValue(key, out var node)) return;
            RemovePendingLocked(node);
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
            double? eta = running && perSecond > 0 ? Math.Round(Math.Max(0, total - completed - failed) / perSecond) : null;
            var queuedByJob = queues.ToDictionary(q => q.Key, q => q.Value.Count);
            var inFlightByJob = inFlight.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
            var jobList = jobs.Values.Select(j => new JobProgress(j.Operation.Prefix, j.Operation.Filter,
                j.Operation.State, queuedByJob.GetValueOrDefault(j.Operation.Key), inFlightByJob.GetValueOrDefault(j.Operation.Key),
                j.Operation.Completed, j.Operation.Total)).ToList();
            return new PreloadStatus(total, completed, failed, running, perSecond, pendingIndex.Count, inFlight.Count,
                activeWorkers, eta, jobList);
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
                var item = disposed ? null : TakeNextLocked();
                if (item is null)
                {
                    activeWorkers--;
                    CheckIdleLocked();
                    return;
                }
                capture = item.Capture;
                jobKey = item.JobKey;
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
                if (disposed) return;
                lock (gate) unsaved--;
                MarkFailed(capture.Key, jobKey);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = saveQueue.Reader;
        var batch = new List<(NewPage Page, string JobKey)>(options.WriteBatchSize);
        var maxDelay = TimeSpan.FromMilliseconds(options.WriteMaxDelayMs);
        try
        {
            while (await reader.WaitToReadAsync(cts.Token).ConfigureAwait(false))
            {
                batch.Clear();
                var started = Stopwatch.GetTimestamp();

                while (batch.Count < options.WriteBatchSize)
                {
                    if (reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        continue;
                    }

                    if (batch.Count == 0) break;
                    var elapsed = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp());
                    var remaining = maxDelay - elapsed;
                    if (remaining <= TimeSpan.Zero) break;

                    try
                    {
                        if (!await reader.WaitToReadAsync(cts.Token).AsTask().WaitAsync(remaining, cts.Token).ConfigureAwait(false))
                            break;
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }
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
        var pages = new NewPage[batch.Length];
        for (var i = 0; i < batch.Length; i++) pages[i] = batch[i].Page;

        var ok = true;
        try
        {
            await store.SavePagesAsync(pages, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ok = false;
            log.LogWarning(ex, "Saving a batch of {Count} pages failed", batch.Length);
        }

        lock (gate)
        {
            var jobCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in batch)
            {
                inFlight.Remove(item.Page.Capture.Key);
                if (jobCounts.TryGetValue(item.JobKey, out var count)) jobCounts[item.JobKey] = count + 1;
                else jobCounts[item.JobKey] = 1;
            }

            completed += batch.Length;
            if (!ok) failed += batch.Length;
            unsaved -= batch.Length;

            foreach (var kv in jobCounts)
            {
                if (!jobs.TryGetValue(kv.Key, out var job)) continue;
                if (ok) UpdateOperationLocked(job, OperationState.Running, kv.Value, 0);
                else UpdateOperationLocked(job, OperationState.Running, 0, kv.Value);
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

    private bool IsBusyLocked() => pendingIndex.Count > 0 || inFlight.Count > 0 || unsaved > 0;

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

    private void SetOperationState(long operationId, OperationState state)
    {
        lock (gate)
        {
            var job = jobs.Values.FirstOrDefault(j => j.Operation.Id == operationId);
            if (job is null || job.Stopping) return;
            job.Paused = state == OperationState.Paused;
            UpdateOperationLocked(job, state);
            if (state == OperationState.Running)
            {
                resumeSignal.TrySetResult();
                resumeSignal = NewSignal(false);
                EnsureWorkersLocked();
            }
        }
    }

    private void EnsureWorkersLocked()
    {
        while (activeWorkers < options.Workers && activeWorkers < pendingIndex.Count)
        {
            activeWorkers++;
            var task = Task.Run(WorkerLoopAsync);
            workerTasks.Add(task);
            _ = task.ContinueWith(_t =>
            {
                lock (gate) workerTasks.Remove(_t);
            }, TaskScheduler.Default);
        }
    }

    private static async Task WaitWithTimeoutAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // shutdown timeout/cancellation: remaining work is resumed on next start
        }
    }

    /// Round-robin across the per-job queues; queues of paused jobs are skipped so they never hold a worker.
    /// Cost is O(number of jobs), not O(queued pages).
    private PendingCapture? TakeNextLocked()
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var pos = (ringCursor + i) % ring.Count;
            var key = ring[pos];
            if (jobs.TryGetValue(key, out var job) && job.Paused) continue;

            var node = queues[key].First!;
            var item = node.Value;
            ringCursor = pos + 1;
            RemovePendingLocked(node);
            if (ring.Count == 0 || ringCursor >= ring.Count) ringCursor = 0;
            lastJobKey = key;
            return item;
        }
        return null;
    }

    private void AddPendingLocked(PendingCapture item)
    {
        if (!queues.TryGetValue(item.JobKey, out var queue))
        {
            queues[item.JobKey] = queue = new LinkedList<PendingCapture>();
            ring.Add(item.JobKey);
        }
        pendingIndex[item.Capture.Key] = queue.AddLast(item);
    }

    private void RemovePendingLocked(LinkedListNode<PendingCapture> node)
    {
        var queue = node.List!;
        var jobKey = node.Value.JobKey;
        queue.Remove(node);
        pendingIndex.Remove(node.Value.Capture.Key);
        if (queue.Count > 0) return;

        queues.Remove(jobKey);
        var index = ring.IndexOf(jobKey);
        if (index < 0) return;
        ring.RemoveAt(index);
        if (index < ringCursor) ringCursor--;
    }

    private void ClearJobQueueLocked(string jobKey)
    {
        if (!queues.TryGetValue(jobKey, out var queue)) return;
        var nodes = new List<LinkedListNode<PendingCapture>>(queue.Count);
        for (var node = queue.First; node is not null; node = node.Next) nodes.Add(node);
        foreach (var node in nodes) RemovePendingLocked(node);
    }

    private void PersistOperation(BackgroundOperation value)
    {
        opWriter ??= Task.Run(OperationWriteLoopAsync);
        opQueue.Writer.TryWrite(value);
    }

    private async Task OperationWriteLoopAsync()
    {
        var reader = opQueue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // only the newest state of each operation matters: coalesce what is queued
            var latest = new Dictionary<long, BackgroundOperation>();
            while (reader.TryRead(out var op)) latest[op.Id] = op;
            foreach (var op in latest.Values)
            {
                try { await store.UpdateOperationAsync(op, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { log.LogWarning(ex, "Saving operation {Id} failed", op.Id); }
            }
        }
    }

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
