using System.Diagnostics;
using WaybackViewer.Models;

namespace WaybackViewer.Services;

// Streams a list's unsaved captures from the database into the preload queue as a bounded, refillable window:
// at most PreloadOptions.MaxQueuedPerJob captures of a job are in memory; the database (keyset cursor) holds the rest.
public sealed partial class PreloadCoordinator
{
    private int WindowSize => Math.Max(1, options.MaxQueuedPerJob);

    /// Queues the unsaved HTML captures of a (filtered) list straight from the database, one window at a time, so a
    /// multi-million-capture list is never materialized. The counts describe the whole search, not the window.
    public async Task<int> EnqueueFilteredAsync(string prefix, string filter, CaptureFilter captureFilter, int htmlTotal,
        int unsavedTotal, int unsavedInView, bool boost = true, CancellationToken ct = default)
    {
        var key = $"{filter}|{prefix}|{captureFilter}";
        var signature = (unsavedInView, (long)htmlTotal, (long)unsavedTotal);
        lock (gate)
        {
            if (disposed || removedJobs.Contains(JobKeyOf(prefix, filter))) return 0;
            if (lastViews.TryGetValue(key, out var last) && last.Signature == signature &&
                Stopwatch.GetElapsedTime(last.Ticks) < ViewThrottle)
                return 0;
            lastViews[key] = (signature, Stopwatch.GetTimestamp());
        }
        return await StartWindowAsync(prefix, filter, captureFilter, (htmlTotal, unsavedTotal), boost, ct).ConfigureAwait(false);
    }

    private static string JobKeyOf(string prefix, string filter) => $"{OperationKind.PagePreload}|{filter}|{prefix}";

    /// (Re)starts the job's database window from the beginning of the given list and queues the first window.
    /// Returns -1 when the store cannot page pending captures (callers fall back to the in-memory list).
    private async Task<int> StartWindowAsync(string prefix, string filter, CaptureFilter source,
        (int Html, int Unsaved)? hint, bool boost, CancellationToken ct)
    {
        var counts = await store.GetPreloadCountsAsync(prefix, filter, ct).ConfigureAwait(false);
        if (counts is null && hint is null) return -1;
        var jobKey = JobKeyOf(prefix, filter);
        JobState? known;
        lock (gate)
        {
            if (disposed || removedJobs.Contains(jobKey)) return 0;
            jobs.TryGetValue(jobKey, out known);
            if (known is { Refilling: true }) return 0;   // a window read is already running for this job
        }

        var operation = known?.Operation ?? await store.CreateOrGetOperationAsync(prefix, filter,
            OperationKind.PagePreload, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        var totals = counts is { } c
            ? (c.Html, c.Html - c.Unsaved, c.Skipped, c.Failed)
            : (hint!.Value.Html, Math.Max(0, hint.Value.Html - hint.Value.Unsaved), 0, 0);

        JobState job;
        lock (gate)
        {
            if (disposed || removedJobs.Contains(jobKey)) return 0;
            if (!jobs.TryGetValue(jobKey, out job!))
                jobs[jobKey] = job = new JobState(operation);
            if (job.Refilling) return 0;
            job.Source = source;
            job.Cursor = null;
            job.Exhausted = false;
            job.ReadFailed = false;
            job.Refilling = true;
        }

        IReadOnlyList<StoredCapture> batch;
        try
        {
            batch = await store.GetUnsavedHtmlBatchAsync(prefix, filter, source, null, WindowSize, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                job.Refilling = false;
                job.Exhausted = true;
                CheckIdleLocked();
            }
            throw;
        }

        var added = EnqueueCore(batch, operation, EmptyOutcomes, totals);
        lock (gate)
        {
            if (batch.Count > 0) job.Cursor = batch[^1];
            job.Exhausted = batch.Count < WindowSize;
            job.Refilling = false;
            CheckJobLocked(job);
            CheckIdleLocked();
            MaybeRefillLocked(jobKey);
        }
        if (boost && batch.Count > 0) Boost(batch.Select(b => b.Capture.Key).Reverse());
        return added;
    }

    /// Called under the lock whenever a job's in-memory queue shrinks: reads the next window from the database once
    /// fewer than half a window is queued. Never more than one read per job at a time.
    private void MaybeRefillLocked(string jobKey)
    {
        if (disposed || !jobs.TryGetValue(jobKey, out var job)) return;
        if (job.Exhausted || job.Refilling || job.Paused || job.Source is null) return;
        var queued = queues.TryGetValue(jobKey, out var q) ? q.Count : 0;
        if (queued >= WindowSize / 2) return;
        job.Refilling = true;
        _ = Task.Run(() => RefillAsync(job, jobKey, WindowSize - queued));
    }

    private async Task RefillAsync(JobState job, string jobKey, int limit)
    {
        CaptureFilter source;
        StoredCapture? cursor;
        lock (gate)
        {
            source = job.Source!;
            cursor = job.Cursor;
        }
        IReadOnlyList<StoredCapture> batch;
        try
        {
            batch = await store.GetUnsavedHtmlBatchAsync(job.Operation.Prefix, job.Operation.Filter, source, cursor,
                Math.Max(1, limit), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            lock (gate)   // shutting down: the rest stays pending in the database and is resumed on the next start
            {
                job.Refilling = false;
                CheckIdleLocked();
            }
            return;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "preload.window-read failed prefix={Prefix} filter={Filter}", job.Operation.Prefix, job.Operation.Filter);
            lock (gate)
            {
                job.Refilling = false;
                job.Exhausted = true;
                job.ReadFailed = true;
                CheckJobLocked(job);
                CheckIdleLocked();
            }
            return;
        }

        (int, int, int, int) totals;
        lock (gate)
        {
            if (!ReferenceEquals(job.Source, source) || !jobs.ContainsKey(jobKey))
            {
                job.Refilling = false;   // the window was restarted or the job removed meanwhile: drop this read
                CheckIdleLocked();
                return;
            }
            if (batch.Count > 0) job.Cursor = batch[^1];
            totals = (job.HtmlCount, job.Saved, job.Skipped, job.Failed);
        }
        if (batch.Count > 0) EnqueueCore(batch, job.Operation, EmptyOutcomes, totals);
        lock (gate)
        {
            job.Exhausted = batch.Count < Math.Max(1, limit);
            job.Refilling = false;
            CheckJobLocked(job);
            CheckIdleLocked();
            MaybeRefillLocked(jobKey);
        }
    }
}
