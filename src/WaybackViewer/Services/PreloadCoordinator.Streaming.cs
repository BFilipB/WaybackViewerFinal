using System.Diagnostics;
using WaybackViewer.Models;

namespace WaybackViewer.Services;

// Streams a filtered list's unsaved captures from the database into the preload queue in bounded batches.
public sealed partial class PreloadCoordinator
{
    private const int StreamBatch = 2000;
    private const int MaxBoosted = 50_000;

    /// Queues the unsaved HTML captures of a (filtered) list straight from the database, so a 100k-capture list is
    /// never materialized for the request. The counts describe the whole search, not the batch.
    public async Task<int> EnqueueFilteredAsync(string prefix, string filter, CaptureFilter captureFilter, int htmlTotal,
        int unsavedTotal, int unsavedInView, bool boost = true, CancellationToken ct = default)
    {
        var key = $"{filter}|{prefix}|{captureFilter}";
        var signature = (unsavedInView, (long)htmlTotal, (long)unsavedTotal);
        var jobKey = $"{OperationKind.PagePreload}|{filter}|{prefix}";
        JobState? known;
        lock (gate)
        {
            if (disposed || removedJobs.Contains(jobKey)) return 0;
            if (lastViews.TryGetValue(key, out var last) && last.Signature == signature &&
                Stopwatch.GetElapsedTime(last.Ticks) < ViewThrottle)
                return 0;
            lastViews[key] = (signature, Stopwatch.GetTimestamp());
            jobs.TryGetValue(jobKey, out known);
        }

        var operation = known?.Operation ?? await store.CreateOrGetOperationAsync(prefix, filter,
            OperationKind.PagePreload, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        var outcomes = await store.GetOutcomesAsync(prefix, filter, ct).ConfigureAwait(false);
        var skipped = outcomes.Values.Count(o => o.Outcome == PreloadOutcome.Skipped);
        var totals = (htmlTotal, Math.Max(0, htmlTotal - unsavedTotal), skipped, outcomes.Count - skipped);

        var added = 0;
        var boosted = boost ? new List<string>() : null;
        StoredCapture? cursor = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await store.GetUnsavedHtmlBatchAsync(prefix, filter, captureFilter, cursor, StreamBatch, ct).ConfigureAwait(false);
            if (batch.Count == 0) break;
            cursor = batch[^1];
            added += EnqueueCore(batch, operation, outcomes, totals);
            if (boosted is not null && boosted.Count < MaxBoosted)
                boosted.AddRange(batch.Where(c => !outcomes.ContainsKey(c.Capture.Key)).Select(c => c.Capture.Key));
            if (batch.Count < StreamBatch) break;
        }
        if (boosted is { Count: > 0 }) Boost(boosted.Take(MaxBoosted).Reverse());   // oldest first, the slideshow order
        return added;
    }
}
