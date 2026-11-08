using WaybackViewer.Models;

namespace WaybackViewer.Storage;

public sealed partial class SqliteArchiveStore
{
    public Task<UrlListPage> GetUrlListAsync(UrlListQuery query, CancellationToken ct = default) =>
        Task.Run(() => { using var conn = Open(); return UrlQueries.List(conn, query); }, ct);

    public Task<UrlSummary?> GetUrlSummaryAsync(long searchId, CancellationToken ct = default) =>
        Task.Run(() => { using var conn = Open(); return UrlQueries.Get(conn, searchId); }, ct);

    public Task<IReadOnlyList<UrlSuggestion>> SuggestUrlsAsync(string prefix, int limit, CancellationToken ct = default) =>
        Task.Run(() => { using var conn = Open(); return UrlQueries.Suggest(conn, prefix, limit); }, ct);

    public Task<CaptureRowsPage> GetSearchCapturesPageAsync(long searchId, UrlCaptureFilter filter, int offset, int limit,
        CancellationToken ct = default) =>
        Task.Run(() => { using var conn = Open(); return UrlQueries.Captures(conn, searchId, filter, offset, limit); }, ct);
}
