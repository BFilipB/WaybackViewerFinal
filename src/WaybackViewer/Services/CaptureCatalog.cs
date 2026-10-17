using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Resolves "all captures under this URL": database first, Wayback CDX only when needed.
public sealed class CaptureCatalog(IArchiveStore store, IArchiveClient archive, IOptions<StorageOptions> options, ITimeSource time)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CatalogResult>>> inflight = new(StringComparer.Ordinal);

    public Task<CatalogResult> GetAsync(string prefix, string filter, bool refresh, CancellationToken ct = default)
    {
        var key = $"{filter}|{refresh}|{prefix}";
        var lazy = inflight.GetOrAdd(key, k => new Lazy<Task<CatalogResult>>(
            () => RunAsync(k, prefix, filter, refresh), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(ct); // concurrent identical requests share one CDX download
    }

    private async Task<CatalogResult> RunAsync(string key, string prefix, string filter, bool refresh)
    {
        try { return await LoadAsync(prefix, filter, refresh).ConfigureAwait(false); }
        finally { inflight.TryRemove(key, out _); }
    }

    private async Task<CatalogResult> LoadAsync(string prefix, string filter, bool refresh)
    {
        var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
        var operation = await store.CreateOrGetOperationAsync(prefix, filter, kind, DateTimeOffset.UtcNow)
            .ConfigureAwait(false);
        operation = operation with { State = OperationState.Running, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateOperationAsync(operation).ConfigureAwait(false);

        SearchSnapshot? saved = null;
        saved = await store.GetSearchAsync(prefix, filter).ConfigureAwait(false);

        if (!refresh && saved is not null && !IsExpired(saved.CachedAt))
        {
            await CompleteAsync(operation, saved.Captures.Count).ConfigureAwait(false);
            return new CatalogResult(saved, true);
        }

        string json;
        try
        {
            json = await archive.GetStringAsync(ArchiveUrls.Cdx(prefix, filter)).ConfigureAwait(false);
        }
        catch (Exception ex) when (saved is not null)
        {
            await CompleteAsync(operation, saved.Captures.Count, "Wayback unavailable; showing the previous cached capture list.")
                .ConfigureAwait(false);
            return new CatalogResult(saved, true); // Wayback unreachable: serve the stale copy instead of failing
        }
        catch (Exception ex)
        {
            await store.UpdateOperationAsync(operation with
            {
                State = OperationState.Failed,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = ex.Message
            }).ConfigureAwait(false);
            throw;
        }

        var captures = CdxParser.Parse(json);
        var now = new DateTimeOffset(DateTime.SpecifyKind(time.UtcNow, DateTimeKind.Utc));
        var snapshot = await store.SaveSearchAsync(prefix, filter, captures, now).ConfigureAwait(false);
        await CompleteAsync(operation, snapshot.Captures.Count).ConfigureAwait(false);
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

    private bool IsExpired(DateTimeOffset cachedAt)
    {
        var days = options.Value.SearchMaxAgeDays;
        if (days <= 0) return false;
        return time.UtcNow - cachedAt.UtcDateTime > TimeSpan.FromDays(days);
    }
}
