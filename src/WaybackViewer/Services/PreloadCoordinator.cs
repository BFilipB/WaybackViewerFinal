using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Background pipeline that downloads every capture that is not saved yet:
///   N download workers (oldest first = slideshow order)  ->  bounded channel (backpressure)  ->  ONE batching writer.
/// Every queued capture ends in a persisted terminal state: saved (page row), skipped or failed (CaptureOutcomes).
/// Temporary failures (429, 5xx, timeouts, network) are retried with backoff before a capture is marked failed.
/// Anything not terminal when the app stops simply stays pending in the database and is resumed on startup.
/// Pages near the slideshow cursor can be moved to the front with <see cref="Boost"/>.
public sealed partial class PreloadCoordinator : IHostedService, IDisposable
{
    private sealed record PendingCapture(Capture Capture, string JobKey);
    private sealed record SaveItem(NewPage? Page, CaptureOutcome? Outcome, string JobKey);

    private sealed class JobState(BackgroundOperation operation)
    {
        public BackgroundOperation Operation = operation;
        public int HtmlCount;
        public int Saved, Skipped, Failed;
        /// Captures of this job that are queued, downloading, waiting for a retry or waiting to be written.
        public int Outstanding;
        public bool Paused;
        public bool SaveFailed;
        /// Speed window: when the job (re)started and how many were saved at that point.
        public long StartTicks = Stopwatch.GetTimestamp();
        public int SavedAtStart;
    }

    private enum FailureKind { Skip, Transient, Permanent }

    private readonly IPageLoader loader;
    private readonly IArchiveStore store;
    private readonly PreloadOptions options;
    private readonly ILogger<PreloadCoordinator> log;

    private readonly object gate = new();
    private readonly Dictionary<string, LinkedList<PendingCapture>> queues = new(StringComparer.Ordinal);
    private readonly List<string> ring = new();
    private int ringCursor;
    private string? priorityKey;
    private bool priorityTurn;
    private readonly Dictionary<string, LinkedListNode<PendingCapture>> pendingIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCapture> retryWaiting = new(StringComparer.Ordinal);
    // Retry counters are independent of the queue/job state, so they are lock-free instead of sharing gate.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);
    private readonly Channel<SaveItem> saveQueue = Channel.CreateBounded<SaveItem>(
        new BoundedChannelOptions(500) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource cts = new();
    private readonly List<Task> workerTasks = new();

    private int total, completed, failed, skipped, activeWorkers, unsaved;
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
        var guardPath = string.IsNullOrWhiteSpace(this.options.DiskGuardPath) ? AppContext.BaseDirectory : this.options.DiskGuardPath;
        FreeDiskBytes = () =>
        {
            var root = Path.GetPathRoot(Path.GetFullPath(guardPath));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        };
    }

    /// Returns the free bytes on the payload drive, or null when unknown. Replaceable in tests.
    public Func<long?> FreeDiskBytes { get; set; } = () => null;
    private long nextDiskCheckTicks;

    private bool LowOnDisk()
    {
        if (options.MinFreeDiskMb <= 0) return false;
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref nextDiskCheckTicks)) return false;
        Interlocked.Exchange(ref nextDiskCheckTicks, now + 5_000);
        var free = FreeDiskBytes();
        if (free is null || free.Value >= options.MinFreeDiskMb * 1024L * 1024L) return false;
        log.LogWarning("preload.disk-low free={FreeMb}MB min={MinMb}MB; pausing preload jobs", free.Value / 1024 / 1024, options.MinFreeDiskMb);
        return true;
    }

    /// Queues every HTML capture that is not saved yet, without an operation. Returns how many were newly queued.
    public int Enqueue(IEnumerable<StoredCapture> captures) =>
        EnqueueCore(captures, null, EmptyOutcomes);

    // ---- per page view ---------------------------------------------------------------------------------
    // Every page view asks for the downloads its list needs. The same view asked again within a few seconds
    // (paging, re-sorting, a reload) changes nothing, so it returns immediately instead of re-checking 100,000 rows.
    private static readonly TimeSpan ViewThrottle = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, ((int Count, long First, long Last) Signature, long Ticks)> lastViews = new();
    private static readonly IReadOnlyDictionary<string, CaptureOutcome> EmptyOutcomes =
        new Dictionary<string, CaptureOutcome>(StringComparer.Ordinal);

    /// Queues what the list needs and moves it ahead of anything queued earlier for a different view.
    public async Task<int> EnqueueViewAsync(IReadOnlyList<StoredCapture> view, string prefix, string filter, bool boost = true,
        CancellationToken ct = default)
    {
        var key = $"{filter}|{prefix}";
        var signature = view.Count == 0 ? (0, 0L, 0L) : (view.Count, view[0].Id, view[^1].Id);
        lock (gate)
        {
            if (lastViews.TryGetValue(key, out var last) && last.Signature == signature &&
                Stopwatch.GetElapsedTime(last.Ticks) < ViewThrottle)
                return 0;
            lastViews[key] = (signature, Stopwatch.GetTimestamp());
        }

        var added = await EnqueueAsync(view, prefix, filter, ct).ConfigureAwait(false);
        if (boost)   // oldest first, which is the slideshow order
            Boost(view.Where(c => c.Capture.IsHtml && !c.Ready).Take(50_000).Reverse().Select(c => c.Capture.Key));
        return added;
    }

    /// Queues every HTML capture of the search that is neither saved nor already skipped/failed, tracked as the
    /// search's PagePreload operation. Returns how many were newly queued.
    public async Task<int> EnqueueAsync(IEnumerable<StoredCapture> captures, string prefix, string filter,
        CancellationToken ct = default)
    {
        JobState? known;
        lock (gate)
        {
            if (disposed || removedJobs.Contains($"{OperationKind.PagePreload}|{filter}|{prefix}")) return 0;
            jobs.TryGetValue($"{OperationKind.PagePreload}|{filter}|{prefix}", out known);
        }
        // Known job: reuse it instead of writing to the database again on every page view.
        var operation = known?.Operation ?? await store.CreateOrGetOperationAsync(prefix, filter,
            OperationKind.PagePreload, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        var outcomes = await store.GetOutcomesAsync(prefix, filter, ct).ConfigureAwait(false);
        return EnqueueCore(captures, operation, outcomes);
    }

    /// Makes the failed captures of a search pending again and queues them. Returns how many were reset.
    public async Task<int> RetryFailedAsync(string prefix, string filter, CancellationToken ct = default)
    {
        var reset = await store.ResetFailedAsync(prefix, filter, ct).ConfigureAwait(false);
        if (reset == 0) return 0;
        lock (gate)
        {
            if (jobs.TryGetValue($"{OperationKind.PagePreload}|{filter}|{prefix}", out var job))
            {
                job.Failed = Math.Max(0, job.Failed - reset);
            }
        }
        var snapshot = await store.GetSearchAsync(prefix, filter, ct).ConfigureAwait(false);
        if (snapshot is not null) await EnqueueAsync(snapshot.Captures, prefix, filter, ct).ConfigureAwait(false);
        log.LogInformation("Retrying {Count} failed captures for {Prefix} ({Filter})", reset, prefix, filter);
        return reset;
    }

    private int EnqueueCore(IEnumerable<StoredCapture> captures, BackgroundOperation? requestedOperation,
        IReadOnlyDictionary<string, CaptureOutcome> outcomes, (int Html, int Ready, int Skipped, int Failed)? totals = null)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["component"] = "preload",
            ["prefix"] = requestedOperation?.Prefix,
            ["filter"] = requestedOperation?.Filter
        });

        // One pass: how many HTML captures there are, how many are saved / skipped / failed, and which still need downloading.
        var htmlCount = 0;
        var readyItems = 0;
        var skippedItems = 0;
        var failedItems = 0;
        var needed = new List<StoredCapture>();
        foreach (var item in captures)
        {
            if (!item.Capture.IsHtml) continue;
            htmlCount++;
            if (item.Ready) readyItems++;
            else if (outcomes.TryGetValue(item.Capture.Key, out var outcome))
            {
                if (outcome.Outcome == PreloadOutcome.Skipped) skippedItems++;
                else failedItems++;
            }
            else needed.Add(item);
        }

        if (totals is { } known)   // streamed batches: the counts describe the whole search, not this batch
            (htmlCount, readyItems, skippedItems, failedItems) = known;

        var jobKey = requestedOperation?.Key ?? string.Empty;
        lock (gate)
        {
            if (disposed) return 0;
            JobState? job = null;
            if (requestedOperation is not null)
            {
                if (!jobs.TryGetValue(jobKey, out job))
                {
                    job = new JobState(requestedOperation);
                    jobs[jobKey] = job;
                }

                job.HtmlCount = Math.Max(job.HtmlCount, htmlCount);
                if (job.Outstanding == 0)
                {
                    // Nothing of this job in memory: the database is the truth.
                    job.SaveFailed = false;
                    job.HtmlCount = htmlCount;
                    job.Saved = readyItems;
                    job.Skipped = skippedItems;
                    job.Failed = failedItems;
                }
            }

            var wasBusy = IsBusyLocked();
            var added = 0;

            List<StoredCapture>? fresh = null;
            foreach (var item in needed)
            {
                var key = item.Capture.Key;
                if (pendingIndex.ContainsKey(key) || inFlight.ContainsKey(key) || retryWaiting.ContainsKey(key)) continue;
                (fresh ??= new List<StoredCapture>()).Add(item);
            }
            if (fresh is not null)
                foreach (var item in OldestFirst(fresh))   // only what is actually new gets ordered
                {
                    AddPendingLocked(new PendingCapture(item.Capture, jobKey));
                    added++;
                }
            if (job is not null) job.Outstanding += added;

            if (added == 0)
            {
                if (job is not null) CheckJobLocked(job);
                return 0;
            }
            if (!wasBusy)
            {
                total = completed = failed = skipped = 0;
                batchStartTicks = Stopwatch.GetTimestamp();
                batchEndTicks = 0;
                idle = NewIdle(false);
            }
            total += added;

            if (job is not null)
            {
                job.Paused = OperationStates.Normalize(job.Operation.State) == OperationState.Paused;
                SyncOperationLocked(job, job.Paused ? OperationState.Paused : OperationState.Running);
            }

            writer ??= Task.Run(WriteLoopAsync);
            EnsureWorkersLocked();
            return added;
        }
    }

    /// Oldest capture first (slideshow order). Lists from the store are newest-first, so reversing is enough and
    /// avoids sorting; anything else is sorted.
    private static IEnumerable<StoredCapture> OldestFirst(List<StoredCapture> items)
    {
        var descending = true;
        for (var i = 1; i < items.Count && descending; i++)
            if (string.CompareOrdinal(items[i - 1].Capture.Timestamp, items[i].Capture.Timestamp) < 0) descending = false;
        if (descending) { items.Reverse(); return items; }
        return items.OrderBy(c => c.Capture.Timestamp, StringComparer.Ordinal);
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
                if (job.Paused) continue;
                job.Paused = true;
                SyncOperationLocked(job, OperationState.Paused);
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
                if (!job.Paused) continue;
                job.Paused = false;
                RestartSpeedLocked(job);
                SyncOperationLocked(job, OperationState.Running);
                CheckJobLocked(job);
            }
            resumeSignal.TrySetResult();
            resumeSignal = NewSignal(false);
            EnsureWorkersLocked();
        }
    }

    public void Pause(long operationId) => SetOperationState(operationId, OperationState.Paused);

    public void Resume(long operationId) => SetOperationState(operationId, OperationState.Running);

    public async Task<IReadOnlyList<BackgroundOperation>> GetOperationsAsync(CancellationToken ct = default) =>
        await store.GetOperationsAsync(activeOnly: true, ct).ConfigureAwait(false);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var operations = await store.GetOperationsAsync(activeOnly: true, cancellationToken).ConfigureAwait(false);
        foreach (var saved in operations.Where(o => o.Kind == OperationKind.PagePreload && o.IsActive))
        {
            var snapshot = await store.GetSearchAsync(saved.Prefix, saved.Filter, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
                await EnqueueAsync(snapshot.Captures, saved.Prefix, saved.Filter, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] workers;
        Task? saveWriter;
        lock (gate)
        {
            disposed = true;
            foreach (var job in jobs.Values.Where(job => job.Outstanding > 0 && !job.Paused))
                SyncOperationLocked(job, OperationState.Interrupted);
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

        // 3) abort what is still downloading or waiting for a retry: it has no outcome yet, so it stays pending in
        //    the database and is queued again on the next start.
        cts.Cancel();

        // 4) flush operation-state writes in order
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
            string jobKey;
            if (pendingIndex.TryGetValue(key, out var node))
            {
                jobKey = node.Value.JobKey;
                RemovePendingLocked(node);
            }
            else if (retryWaiting.Remove(key, out var waiting)) jobKey = waiting.JobKey;
            else return;

            attempts.TryRemove(key, out _);
            total--;
            if (jobs.TryGetValue(jobKey, out var job))
            {
                job.Outstanding--;
                job.Saved++;
                SyncOperationLocked(job, job.Operation.State);
                CheckJobLocked(job);
            }
            CheckIdleLocked();
        }
    }

    private readonly HashSet<string> removedJobs = new(StringComparer.Ordinal);

    /// Drops the PagePreload job of a searched URL (its queued captures are forgotten, downloads in flight finish and
    /// are discarded by the delete that follows). The job cannot be re-created until <see cref="ReleaseJob"/>.
    public async Task RemoveJobAsync(string prefix, string filter, CancellationToken ct = default)
    {
        var jobKey = $"{OperationKind.PagePreload}|{filter}|{prefix}";
        lock (gate)
        {
            removedJobs.Add(jobKey);
            if (jobs.TryGetValue(jobKey, out var job))
            {
                var cleared = ClearJobQueueLocked(jobKey);
                job.Outstanding -= cleared;
                foreach (var (key, waiting) in retryWaiting.Where(p => p.Value.JobKey == jobKey).ToList())
                {
                    retryWaiting.Remove(key);
                    attempts.TryRemove(key, out _);
                    total--;
                    job.Outstanding--;
                }
            }
            CheckIdleLocked();
        }
        // wait for what is already downloading or being written for this job
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!jobs.TryGetValue(jobKey, out var job) || job.Outstanding <= 0)
                {
                    jobs.Remove(jobKey);
                    return;
                }
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    /// Allows the PagePreload job of this searched URL to be created again (after a delete).
    public void ReleaseJob(string prefix, string filter)
    {
        lock (gate) removedJobs.Remove($"{OperationKind.PagePreload}|{filter}|{prefix}");
    }

    /// True when this search's PagePreload job is loaded in memory (queued, running, paused or finished this run).
    public bool HasJob(string prefix, string filter)
    {
        lock (gate) return jobs.ContainsKey($"{OperationKind.PagePreload}|{filter}|{prefix}");
    }

    /// Gives the PagePreload operation of this search a larger share of the workers (the URL open on Home).
    /// Other operations keep being served round-robin. Null clears the boost.
    public void SetPriority(string? prefix, string? filter)
    {
        lock (gate) priorityKey = prefix is null || filter is null ? null : $"{OperationKind.PagePreload}|{filter}|{prefix}";
    }

    private void RestartSpeedLocked(JobState job)
    {
        job.StartTicks = Stopwatch.GetTimestamp();
        job.SavedAtStart = job.Saved;
    }

    private double JobSpeedLocked(JobState job)
    {
        if (job.Paused || job.Outstanding == 0) return 0;
        var seconds = Stopwatch.GetElapsedTime(job.StartTicks).TotalSeconds;
        var done = job.Saved - job.SavedAtStart;
        return seconds > 0.25 && done > 0 ? Math.Round(done / seconds, 2) : 0;
    }

    private double? JobEtaLocked(JobState job)
    {
        var speed = JobSpeedLocked(job);
        return speed > 0 ? Math.Round(job.Outstanding / speed) : null;
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
            double? eta = running && perSecond > 0 ? Math.Round(Math.Max(0, total - completed) / perSecond) : null;
            var queuedByJob = queues.ToDictionary(q => q.Key, q => q.Value.Count);
            var inFlightByJob = inFlight.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());
            var jobList = jobs.Values.Select(j => new JobProgress(j.Operation.Prefix, j.Operation.Filter,
                j.Operation.State, queuedByJob.GetValueOrDefault(j.Operation.Key), inFlightByJob.GetValueOrDefault(j.Operation.Key),
                j.Operation.Completed, j.Operation.Total, j.Failed, j.Skipped, JobSpeedLocked(j), JobEtaLocked(j))).ToList();
            return new PreloadStatus(total, completed, failed, running, perSecond, pendingIndex.Count + retryWaiting.Count,
                inFlight.Count, activeWorkers, eta, jobList, skipped);
        }
    }

    /// Completes when nothing is queued, downloading, waiting for a retry or waiting to be saved (used by tests).
    public Task WhenIdleAsync()
    {
        lock (gate) return idle.Task;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed && cts.IsCancellationRequested) return;
            disposed = true;
        }
        cts.Cancel();
        saveQueue.Writer.TryComplete();
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            PendingCapture item;
            lock (gate)
            {
                var next = disposed ? null : TakeNextLocked();
                if (next is null)
                {
                    activeWorkers--;
                    CheckIdleLocked();
                    return;
                }
                item = next;
                inFlight[item.Capture.Key] = item.JobKey;
            }

            if (LowOnDisk())
                Pause();

            await WaitIfPausedAsync(item.JobKey).ConfigureAwait(false);

            NewPage page;
			var downloadStart = Stopwatch.GetTimestamp();
			try
			{
				page = await loader.LoadAsync(item.Capture, cts.Token).ConfigureAwait(false);
				log.LogDebug("preload.download key={Key} host={Host} ms={Ms:F0} htmlGzBytes={Bytes}", item.Capture.Key, HostOf(item.Capture.Original),
					Stopwatch.GetElapsedTime(downloadStart).TotalMilliseconds, page.HtmlGz.Length);
			}
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                AbandonLocked(item);   // shutting down: stays pending in the database
                return;
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(item, ex).ConfigureAwait(false);
                continue;
            }

            if (!await TryQueueSaveAsync(new SaveItem(page, null, item.JobKey), item).ConfigureAwait(false)) return;
        }
    }

    /// Hands a result to the writer. Waits while the save queue is full (backpressure); never drops the result
    /// except at shutdown, where the capture simply stays pending in the database.
    private async Task<bool> TryQueueSaveAsync(SaveItem result, PendingCapture item)
    {
        Interlocked.Increment(ref unsaved);
        try
        {
            await saveQueue.Writer.WriteAsync(result, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            Interlocked.Decrement(ref unsaved);
            AbandonLocked(item);
            return false;
        }
    }

    private static string HostOf(string original) =>
        Uri.TryCreate(original, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "unknown";

    private void AbandonLocked(PendingCapture item)
    {
        lock (gate)
        {
            inFlight.Remove(item.Capture.Key);
            if (jobs.TryGetValue(item.JobKey, out var job)) job.Outstanding--;
            CheckIdleLocked();
        }
    }

    private async Task HandleFailureAsync(PendingCapture item, Exception ex)
    {
        var (kind, reason, retryAfter) = Classify(ex);
        var key = item.Capture.Key;

        if (kind == FailureKind.Transient)
        {
            int attempt;
            attempt = attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
            if (attempt < Math.Max(1, options.MaxAttempts))
            {
                var delay = RetryDelay(attempt, retryAfter);
                log.LogInformation("preload.retry key={Key} host={Host} reason={Reason} attempt={Attempt} delayMs={Delay:F0}",
                    key, HostOf(item.Capture.Original), reason, attempt, delay.TotalMilliseconds);
                lock (gate)
                {
                    inFlight.Remove(key);
                    retryWaiting[key] = item;
                }
                _ = RequeueAfterAsync(item, delay);
                return;
            }
            reason += "-retries-exhausted";
        }

        var outcome = kind == FailureKind.Skip ? PreloadOutcome.Skipped : PreloadOutcome.Failed;
        if (outcome == PreloadOutcome.Failed)
            log.LogWarning(ex, "preload.failed key={Key} host={Host} reason={Reason} kind={Kind}", key, HostOf(item.Capture.Original), reason, kind);
        else
            log.LogInformation("preload.skipped key={Key} host={Host} reason={Reason}", key, HostOf(item.Capture.Original), reason);

        attempts.TryRemove(key, out _);
        await TryQueueSaveAsync(new SaveItem(null, new CaptureOutcome(item.Capture, outcome, reason, DateTimeOffset.UtcNow),
            item.JobKey), item).ConfigureAwait(false);
    }

    private async Task RequeueAfterAsync(PendingCapture item, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down: the capture has no outcome, so it stays pending and is resumed on the next start
        }

        lock (gate)
        {
            if (!retryWaiting.Remove(item.Capture.Key)) return;   // done some other way meanwhile
            if (disposed)
            {
                if (jobs.TryGetValue(item.JobKey, out var job)) job.Outstanding--;
                CheckIdleLocked();
                return;
            }
            AddPendingLocked(item);
            EnsureWorkersLocked();
        }
    }

    private TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter)
    {
        var max = TimeSpan.FromMilliseconds(Math.Max(1, options.RetryMaxDelayMs));
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(Math.Max(1, options.RetryBaseDelayMs) * Math.Pow(2, attempt - 1));
        return delay < max ? delay : max;
    }

    private static (FailureKind Kind, string Reason, TimeSpan? RetryAfter) Classify(Exception ex)
    {
        switch (ex)
        {
            case InvalidDataException:
                return (FailureKind.Skip, "non-html", null);
            case HttpRequestException { StatusCode: { } status } http:
                var code = (int)status;
                var retryAfter = (http as ArchiveRequestException)?.RetryAfter;
                if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) return (FailureKind.Skip, "not-found", null);
                if (code is >= 300 and < 400) return (FailureKind.Skip, "redirect", null);
                if (code is 408 or 429 || code >= 500) return (FailureKind.Transient, $"http-{code}", retryAfter);
                return (FailureKind.Permanent, $"http-{code}", null);
            case HttpRequestException or IOException:
                return (FailureKind.Transient, "network", null);
            case TimeoutException or OperationCanceledException:   // not ours: shutdown is handled by the caller
                return (FailureKind.Transient, "timeout", null);
            default:
                return (FailureKind.Permanent, "error", null);
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = saveQueue.Reader;
        var batch = new List<SaveItem>(options.WriteBatchSize);
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
            // shutting down: unwritten results have no outcome yet and are resumed on the next start
        }
    }

    /// Writes one batch. A failing database write is retried (with backoff) until it succeeds or the app stops;
    /// it never turns downloaded pages into failed captures.
    private long summaryTicks = Stopwatch.GetTimestamp();
	private int summaryPages;

	private async Task FlushAsync(SaveItem[] batch)
    {
        var pages = batch.Where(i => i.Page is not null).Select(i => i.Page!).ToArray();
        var outcomes = batch.Where(i => i.Outcome is not null).Select(i => i.Outcome!).ToArray();

        var flushStart = Stopwatch.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (pages.Length > 0) await store.SavePagesAsync(pages, CancellationToken.None).ConfigureAwait(false);
                if (outcomes.Length > 0) await store.SaveOutcomesAsync(outcomes, CancellationToken.None).ConfigureAwait(false);
                log.LogInformation("preload.flush pages={Pages} outcomes={Outcomes} attempt={Attempt} ms={Ms:F0} saveQueue={Queue} activeWorkers={Workers}",
                    pages.Length, outcomes.Length, attempt, Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds, saveQueue.Reader.Count, activeWorkers);
				summaryPages += pages.Length;
				if (Stopwatch.GetElapsedTime(summaryTicks).TotalSeconds >= 10)
				{
					var secs = Stopwatch.GetElapsedTime(summaryTicks).TotalSeconds;
					log.LogInformation("preload.summary savedPerSec={Rate:F2} total={Total} completed={Completed} failed={Failed} skipped={Skipped} activeWorkers={Workers} unsaved={Unsaved} saveQueue={Queue}",
						summaryPages / secs, total, completed, failed, skipped, activeWorkers, unsaved, saveQueue.Reader.Count);
					summaryPages = 0;
					summaryTicks = Stopwatch.GetTimestamp();
				}
                break;
            }
            catch (Exception ex) when (options.SaveMaxAttempts > 0 && attempt >= options.SaveMaxAttempts)
            {
                log.LogError(ex, "Saving a batch of {Count} preload results failed {Attempt} times; giving up", batch.Length, attempt);
                GiveUpSaveLocked(batch);
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Saving a batch of {Count} preload results failed (attempt {Attempt}); retrying", batch.Length, attempt);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30_000, 100 * Math.Pow(2, Math.Min(attempt, 10)))), cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (gate)
                    {
                        Interlocked.Add(ref unsaved, -batch.Length);
                        foreach (var item in batch)
                        {
                            var capture = item.Page?.Capture ?? item.Outcome!.Capture;
                            inFlight.Remove(capture.Key);
                            if (jobs.TryGetValue(item.JobKey, out var job)) job.Outstanding--;
                        }
                        CheckIdleLocked();
                    }
                    throw;
                }
            }
        }

        lock (gate)
        {
            var touched = new HashSet<JobState>();
            foreach (var item in batch)
            {
                var capture = item.Page?.Capture ?? item.Outcome!.Capture;
                inFlight.Remove(capture.Key);
                attempts.TryRemove(capture.Key, out _);
                completed++;
                if (item.Outcome?.Outcome == PreloadOutcome.Failed) failed++;
                else if (item.Outcome?.Outcome == PreloadOutcome.Skipped) skipped++;

                if (!jobs.TryGetValue(item.JobKey, out var job)) continue;
                job.Outstanding--;
                if (item.Page is not null) job.Saved++;
                else if (item.Outcome!.Outcome == PreloadOutcome.Skipped) job.Skipped++;
                else job.Failed++;
                touched.Add(job);
            }
            Interlocked.Add(ref unsaved, -batch.Length);

            foreach (var job in touched)
            {
                SyncOperationLocked(job, job.Operation.State);
                CheckJobLocked(job);
            }
            CheckIdleLocked();
        }
    }

    /// The database kept failing: the batch is dropped without an outcome (so it is retried on resume/restart)
    /// and its jobs end as Failed instead of waiting forever.
    private void GiveUpSaveLocked(SaveItem[] batch)
    {
        lock (gate)
        {
            var touched = new HashSet<JobState>();
            foreach (var item in batch)
            {
                var capture = item.Page?.Capture ?? item.Outcome!.Capture;
                inFlight.Remove(capture.Key);
                attempts.TryRemove(capture.Key, out _);
                if (!jobs.TryGetValue(item.JobKey, out var job)) continue;
                job.Outstanding--;
                job.SaveFailed = true;
                touched.Add(job);
            }
            Interlocked.Add(ref unsaved, -batch.Length);
            foreach (var job in touched) CheckJobLocked(job);
            CheckIdleLocked();
        }
    }

    private bool IsBusyLocked() => pendingIndex.Count > 0 || inFlight.Count > 0 || retryWaiting.Count > 0 || Volatile.Read(ref unsaved) > 0;

    /// A job is finished once every one of its captures has a persisted terminal state.
    private void CheckJobLocked(JobState job)
    {
        if (disposed || job.Outstanding > 0 || job.Paused) return;   // after shutdown the job stays Interrupted
        if (job.Operation.State is not (OperationState.Running or OperationState.Queued)) return;
        SyncOperationLocked(job, job.SaveFailed ? OperationState.Failed : job.Failed > 0 ? OperationState.CompletedWithFailures : OperationState.Completed);
    }

    private void CheckIdleLocked()
    {
        foreach (var job in jobs.Values) CheckJobLocked(job);
        if (IsBusyLocked()) return;
        if (batchEndTicks == 0) batchEndTicks = Stopwatch.GetTimestamp();
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

    /// Copies the job's counters into its operation and persists it. Total = captures that can be preloaded
    /// (HTML captures minus skipped ones); Completed = saved; Failed = failed.
    private void SyncOperationLocked(JobState job, OperationState state)
    {
        if (!OperationStates.CanTransition(job.Operation.State, state))
        {
            log.LogWarning("Rejected operation {Id} transition {From} -> {To}", job.Operation.Id, job.Operation.State, state);
            state = job.Operation.State;
        }
        var preloadable = Math.Max(0, job.HtmlCount - job.Skipped);
		if (state != job.Operation.State)
			log.LogInformation("operation.state id={Id} kind={Kind} {From}->{To} prefix={Prefix} filter={Filter} total={Total} saved={Saved} failed={Failed} skipped={Skipped}",
				job.Operation.Id, job.Operation.Kind, job.Operation.State, state, job.Operation.Prefix, job.Operation.Filter, preloadable, job.Saved, job.Failed, job.Skipped);
		if (state != job.Operation.State && state is OperationState.Completed or OperationState.CompletedWithFailures or OperationState.Failed)
{
var secs = Stopwatch.GetElapsedTime(job.StartTicks).TotalSeconds;
log.LogInformation("operation.summary id={Id} kind={Kind} state={State} prefix={Prefix} filter={Filter} total={Total} saved={Saved} failed={Failed} skipped={Skipped} elapsedSec={Elapsed:F0} avgPerSec={Rate:F2}",
job.Operation.Id, job.Operation.Kind, state, job.Operation.Prefix, job.Operation.Filter, preloadable, job.Saved, job.Failed, job.Skipped,
secs, secs > 0 ? (job.Saved - job.SavedAtStart) / secs : 0);
}
job.Operation = job.Operation with
        {
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            Total = preloadable,
            Completed = Math.Min(preloadable, job.Saved),
            Failed = job.Failed,
            Error = state == OperationState.Failed && job.SaveFailed ? "Saving downloaded pages to the database kept failing."
                : state == OperationState.CompletedWithFailures ? $"{job.Failed} captures could not be downloaded." : null
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
            if (job is null) return;
            job.Paused = state == OperationState.Paused;
            SyncOperationLocked(job, state);
            if (state == OperationState.Running)
            {
                CheckJobLocked(job);
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
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // shutdown timeout/cancellation: remaining work has no outcome yet and is resumed on next start
        }
    }

    /// Round-robin across the per-job queues; queues of paused jobs are skipped so they never hold a worker.
    /// Cost is O(number of jobs), not O(queued pages).
    private PendingCapture? TakeNextLocked()
    {
        // Priority boost: the URL open on Home gets every other pick, so it is faster but never starves the rest.
        priorityTurn = !priorityTurn;
        if (priorityTurn && priorityKey is not null && queues.TryGetValue(priorityKey, out var boosted) &&
            !(jobs.TryGetValue(priorityKey, out var pj) && pj.Paused))
        {
            var first = boosted.First!.Value;
            RemovePendingLocked(boosted.First);
            return first;
        }
        for (var i = 0; i < ring.Count; i++)
        {
            var pos = (ringCursor + i) % ring.Count;
            var key = ring[pos];
            if (jobs.TryGetValue(key, out var job) && job.Paused) continue;

            var node = queues[key].First!;
            var item = node.Value;
            ringCursor = pos + 1;
            RemovePendingLocked(node);   // the cursor may now equal ring.Count; the modulo above wraps it lazily, so a job added meanwhile gets the next turn
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

    private int ClearJobQueueLocked(string jobKey)
    {
        if (!queues.TryGetValue(jobKey, out var queue)) return 0;
        var nodes = new List<LinkedListNode<PendingCapture>>(queue.Count);
        for (var node = queue.First; node is not null; node = node.Next) nodes.Add(node);
        foreach (var node in nodes) RemovePendingLocked(node);
        total -= nodes.Count;
        return nodes.Count;
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
