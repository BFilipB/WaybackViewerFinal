using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Resolves "all captures under this URL": database first, Wayback CDX only when needed.
public sealed class CaptureCatalog(IArchiveStore store, IArchiveClient archive, IOptions<StorageOptions> options, ITimeSource time,
    ILogger<CaptureCatalog>? logger = null)
{
    private readonly ILogger log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly ConcurrentDictionary<string, Lazy<Task<CatalogResult>>> inflight = new(StringComparer.Ordinal);

    public Task<CatalogResult> GetAsync(string prefix, string filter, bool refresh, CancellationToken ct = default)
    {
        var key = $"{filter}|{refresh}|{prefix}";
        var lazy = inflight.GetOrAdd(key, k => new Lazy<Task<CatalogResult>>(
            () => RunAsync(k, prefix, filter, refresh), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(ct); // concurrent identical requests share one CDX download
    }

    /// Raised after a capture list was downloaded and saved (initial load or refresh), so new captures can be preloaded.
    public event Func<string, string, SearchSnapshot, Task>? Saved;

    private readonly object refreshLock = new();
    private readonly HashSet<string> refreshingKeys = new(StringComparer.Ordinal);
    private readonly List<Task> refreshTasks = new();

    private void StartBackgroundRefresh(string prefix, string filter)
    {
        var key = filter + "|" + prefix;
        lock (refreshLock)
        {
            if (!refreshingKeys.Add(key)) return;   // already updating
            refreshTasks.RemoveAll(t => t.IsCompleted);
            refreshTasks.Add(Task.Run(async () =>
            {
                try { await GetAsync(prefix, filter, refresh: true).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the saved list stays; the failed operation is recorded by LoadAsync
                    log.LogWarning(ex, "catalog.background_refresh_failed prefix={Prefix} filter={Filter} errorType={Type}", prefix, filter, ex.GetType().Name);
                }
                finally { lock (refreshLock) refreshingKeys.Remove(key); }
            }));
        }
    }

    /// Completes when no background refresh is running (used by tests).
    public Task WhenRefreshesDoneAsync()
    {
        lock (refreshLock) return Task.WhenAll(refreshTasks.ToArray());
    }

    /// Freshness of a saved, unexpired list without loading its captures; null when a load/refresh is needed.
    public async Task<CatalogState?> TryGetFreshStateAsync(string prefix, string filter, bool refresh, CancellationToken ct = default)
    {
        if (refresh) return null;
        var head = await store.GetSearchPageAsync(prefix, filter, 0, 0, ct).ConfigureAwait(false);
        return head is null || IsExpired(head.CachedAt) ? null : new CatalogState(head.CachedAt, true, false);
    }

    public async Task<bool> HasFreshSnapshotAsync(string prefix, string filter, CancellationToken ct = default)
    {
        var head = await store.GetSearchPageAsync(prefix, filter, 0, 0, ct).ConfigureAwait(false);   // metadata only, no capture rows
        return head is not null && !IsExpired(head.CachedAt);
    }

    private async Task<CatalogResult> RunAsync(string key, string prefix, string filter, bool refresh)
    {
        try { return await LoadAsync(prefix, filter, refresh).ConfigureAwait(false); }
        finally { inflight.TryRemove(key, out _); }
    }

    private async Task<CatalogResult> LoadAsync(string prefix, string filter, bool refresh)
    {
        SearchSnapshot? saved = await store.GetSearchAsync(prefix, filter).ConfigureAwait(false);

        if (!refresh && saved is not null)
        {
            if (!IsExpired(saved.CachedAt))
            {
                log.LogInformation("catalog.load source=db prefix={Prefix} filter={Filter} rows={Rows} ageHours={Age:F1}", prefix, filter, saved.Captures.Count, (time.UtcNow - saved.CachedAt.UtcDateTime).TotalHours);
                return new CatalogResult(saved, true);
            }
            log.LogInformation("catalog.expired prefix={Prefix} filter={Filter} ageHours={Age:F1} maxDays={Max}", prefix, filter, (time.UtcNow - saved.CachedAt.UtcDateTime).TotalHours, options.Value.SearchMaxAgeDays);

            // Stale snapshots must revalidate before returning the result so the visible list reflects current data.
            // This keeps the behavior consistent with the cache-refresh tests and avoids serving old data as if it were current.
        }

        var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
        var operation = await store.CreateOrGetOperationAsync(prefix, filter, kind, DateTimeOffset.UtcNow)
            .ConfigureAwait(false);
        if (OperationStates.Normalize(operation.State) == OperationState.Paused && saved is not null)
        {
            log.LogInformation("catalog.load source=db_paused prefix={Prefix} filter={Filter} rows={Rows}", prefix, filter, saved.Captures.Count);
            return new CatalogResult(saved, true);   // paused by the user: keep serving the saved list, do not download
        }
        operation = operation with { State = OperationState.Running, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateOperationAsync(operation).ConfigureAwait(false);

        var cdxStart = System.Diagnostics.Stopwatch.GetTimestamp();
        string json;
        try
        {
            json = await archive.GetStringAsync(ArchiveUrls.Cdx(prefix, filter)).ConfigureAwait(false);
        }
        catch (Exception ex) when (saved is not null)
        {
            log.LogWarning(ex, "catalog.stale_fallback prefix={Prefix} filter={Filter} rows={Rows} cdxMs={Ms:F0} errorType={Type}", prefix, filter, saved.Captures.Count, System.Diagnostics.Stopwatch.GetElapsedTime(cdxStart).TotalMilliseconds, ex.GetType().Name);
            await CompleteAsync(operation, saved.Captures.Count, "Wayback unavailable; showing the previous cached capture list.")
                .ConfigureAwait(false);
            return new CatalogResult(saved, true); // Wayback unreachable: serve the stale copy instead of failing
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "catalog.cdx_failed prefix={Prefix} filter={Filter} cdxMs={Ms:F0} errorType={Type}", prefix, filter, System.Diagnostics.Stopwatch.GetElapsedTime(cdxStart).TotalMilliseconds, ex.GetType().Name);
            await store.UpdateOperationAsync(operation with
            {
                State = OperationState.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = DescribeFailure(ex)
            }).ConfigureAwait(false);
            throw;
        }

        var cdxMs = System.Diagnostics.Stopwatch.GetElapsedTime(cdxStart).TotalMilliseconds;
        var parseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var captures = CdxParser.Parse(json, out var skippedRows);
        var parseMs = System.Diagnostics.Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;
        if (skippedRows > 0)
            log.LogWarning("catalog.parse_skipped prefix={Prefix} filter={Filter} skippedRows={Skipped} rows={Rows}", prefix, filter, skippedRows, captures.Count);
        var saveStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = new DateTimeOffset(DateTime.SpecifyKind(time.UtcNow, DateTimeKind.Utc));
        var snapshot = await store.SaveSearchAsync(prefix, filter, captures, now).ConfigureAwait(false);
        log.LogInformation("catalog.load source=cdx prefix={Prefix} filter={Filter} refresh={Refresh} rows={Rows} jsonChars={Chars} cdxMs={Cdx:F0} parseMs={Parse:F0} saveMs={Save:F0} hadSaved={HadSaved}",
            prefix, filter, refresh, snapshot.Captures.Count, json.Length, cdxMs, parseMs, System.Diagnostics.Stopwatch.GetElapsedTime(saveStart).TotalMilliseconds, saved is not null);
        if (saved is not null)
            log.LogInformation("catalog.refresh_delta prefix={Prefix} filter={Filter} before={Before} after={After} added={Added} ageHours={Age:F1}",
                prefix, filter, saved.Captures.Count, snapshot.Captures.Count, snapshot.Captures.Count - saved.Captures.Count, (time.UtcNow - saved.CachedAt.UtcDateTime).TotalHours);
        await CompleteAsync(operation, snapshot.Captures.Count).ConfigureAwait(false);
        if (Saved is { } handler)
            foreach (var h in handler.GetInvocationList().Cast<Func<string, string, SearchSnapshot, Task>>())
                await h(prefix, filter, snapshot).ConfigureAwait(false);
        return new CatalogResult(snapshot, false);
    }

    private async Task CompleteAsync(BackgroundOperation operation, int count, string? error = null)
    {
        await store.UpdateOperationAsync(operation with
        {
            State = OperationState.Completed,
            UpdatedAt = DateTimeOffset.UtcNow,
            Total = count,
            Completed = count,
            Error = error
        }).ConfigureAwait(false);
    }

    /// Client-safe description of a capture-list failure (never the raw exception message).
	/// Worth retrying later: network errors, timeouts, 408/429 and 5xx. Not-found/other 4xx and bugs are permanent.
	public static bool IsTransientFailure(Exception ex) => ex switch
	{
		ArchiveRequestException a => (int)a.StatusCode is 408 or 429 or >= 500,
		HttpRequestException or TimeoutException or IOException => true,
		TaskCanceledException => true,   // HttpClient timeout (caller cancellation is checked before this)
		_ => false
	};

	public static string DescribeFailure(Exception ex) => ex switch
    {
        ArchiveRequestException a => $"Wayback answered HTTP {(int)a.StatusCode}.",
        HttpRequestException => "Wayback could not be reached.",
        TimeoutException or TaskCanceledException => "Wayback did not answer in time.",
        _ => "Loading the capture list failed."
    };

    private bool IsExpired(DateTimeOffset cachedAt)
    {
        var days = options.Value.SearchMaxAgeDays;
        if (days <= 0) return false;
        return time.UtcNow - cachedAt.UtcDateTime > TimeSpan.FromDays(days);
    }
}
