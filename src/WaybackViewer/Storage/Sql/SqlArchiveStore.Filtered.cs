using WaybackViewer.Models;

namespace WaybackViewer.Storage.Sql;

// Facet-filtered, content-searched and popularity-ranked pages for any SQL provider (see FilteredQueries).
public sealed partial class SqlArchiveStore
{
    private readonly FacetMemo facetMemo = new();

    private async Task EnsureClassifiedAsync(CancellationToken ct)
    {
        var pending = await Task.Run(() =>
        {
            using var conn = Open();
            return FilteredQueries.HasUnclassified(conn);
        }, ct).ConfigureAwait(false);
        if (!pending) return;
        await WriteAsync(() =>
        {
            using var conn = Open();
            FilteredQueries.ClassifyPending(conn, ct);
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<FilteredPage?> GetFilteredPageAsync(string prefix, string filter, CaptureFilter captureFilter, int page, int pageSize,
        string? contentTerm = null, CancellationToken ct = default)
    {
        if (captureFilter.Text is not null && !dialect.SupportsUrlMatch) return null;   // no SQL-side matcher: in-memory path
        await EnsureClassifiedAsync(ct).ConfigureAwait(false);
        return await ReadFilteredAsync(prefix, filter, captureFilter, page, pageSize, contentTerm, null, ct).ConfigureAwait(false);
    }

    public async Task<FilteredPage?> GetPopularPageAsync(string prefix, string filter, CaptureFilter captureFilter, int page, int pageSize,
        string? contentTerm, PopularityQuery popularity, CancellationToken ct = default)
    {
        if (captureFilter.Text is not null && !dialect.SupportsUrlMatch) return null;
        await EnsureClassifiedAsync(ct).ConfigureAwait(false);
        var stale = await Task.Run(() =>
        {
            using var conn = Open();
            return FilteredQueries.SignalsStale(conn, prefix, filter);
        }, ct).ConfigureAwait(false);
        if (stale)
            await WriteAsync(() =>
            {
                FilteredQueries.BuildSignals(Open, dialect, prefix, filter, ct);
                return 0;
            }, ct).ConfigureAwait(false);
        return await ReadFilteredAsync(prefix, filter, captureFilter, page, pageSize, contentTerm, popularity, ct).ConfigureAwait(false);
    }

    private Task<FilteredPage?> ReadFilteredAsync(string prefix, string filter, CaptureFilter f, int page, int pageSize,
        string? contentTerm, PopularityQuery? popularity, CancellationToken ct)
    {
        long version;
        lock (cacheLock) version = savedVersion;
        return Task.Run(() =>
        {
            using var conn = Open();
            return FilteredQueries.ReadPage(conn, dialect, facetMemo, version, prefix, filter, f, page, Math.Max(1, pageSize), contentTerm, popularity, ct);
        }, ct);
    }

	public Task<IReadOnlyList<StoredCapture>?> GetUrlHistoryAsync(string prefix, string filter, string original, int limit,
		CancellationToken ct = default) =>
		Task.Run(() =>
		{
			using var conn = Open();
			return FilteredQueries.ReadHistory(conn, prefix, filter, original, limit);
		}, ct);

	public Task<PreloadCounts?> GetPreloadCountsAsync(string prefix, string filter, CancellationToken ct = default) =>
		Task.Run(() =>
		{
			using var conn = Open();
			return FilteredQueries.ReadPreloadCounts(conn, prefix, filter, ct);
		}, ct);

	public async Task<IReadOnlyList<StoredCapture>> GetUnsavedHtmlBatchAsync(string prefix, string filter, CaptureFilter captureFilter,
        StoredCapture? after, int limit, CancellationToken ct = default)
    {
        if (captureFilter.Text is not null && !dialect.SupportsUrlMatch) return [];
        await EnsureClassifiedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var conn = Open();
            return FilteredQueries.ReadUnsavedBatch(conn, prefix, filter, captureFilter, after, limit);
        }, ct).ConfigureAwait(false);
    }
}
