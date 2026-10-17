using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// Volatile store: used by the tests and for "Storage:Provider": "Memory". Mirrors SqlArchiveStore semantics.
public sealed class InMemoryArchiveStore : IArchiveStore
{
    private readonly object gate = new();
    private long nextCaptureId = 1;
    private long nextSearchId = 1;
    private long nextOperationId = 1;
    private readonly Dictionary<string, long> captureIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Capture> captures = new();
    private readonly Dictionary<long, StoredPage> pages = new();
    private readonly Dictionary<string, (long Id, DateTimeOffset CachedAt, List<long> CaptureIds)> searches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BackgroundOperation> operations = new(StringComparer.Ordinal);
    private readonly List<int> saveBatchSizes = new();

    /// Sizes of every SavePagesAsync call (lets tests verify batching).
    public IReadOnlyList<int> SaveBatchSizes { get { lock (gate) return saveBatchSizes.ToArray(); } }
    public int PageCount { get { lock (gate) return pages.Count; } }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<SearchSnapshot?> GetSearchAsync(string prefix, string filter, CancellationToken ct = default)
    {
        lock (gate)
        {
            return Task.FromResult(searches.TryGetValue(SearchKey(prefix, filter), out var s)
                ? BuildSnapshot(s.Id, s.CachedAt, s.CaptureIds)
                : null);
        }
    }

    public Task<PagedSearchResult?> GetSearchPageAsync(string prefix, string filter, int offset, int limit,
        CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!searches.TryGetValue(SearchKey(prefix, filter), out var search))
                return Task.FromResult<PagedSearchResult?>(null);

            var page = search.CaptureIds
                .Select(id => new StoredCapture(id, captures[id], pages.ContainsKey(id)))
                .OrderByDescending(c => c.Capture.Timestamp)
                .ThenByDescending(c => c.Id)
                .Skip(Math.Max(0, offset))
                .Take(Math.Max(0, limit))
                .ToArray();
            return Task.FromResult<PagedSearchResult?>(new PagedSearchResult(
                search.Id, search.CachedAt, page, search.CaptureIds.Count));
        }
    }

    public Task<SearchSnapshot> SaveSearchAsync(string prefix, string filter, IReadOnlyList<Capture> list,
        DateTimeOffset cachedAt, CancellationToken ct = default)
    {
        lock (gate)
        {
            var ids = new List<long>(list.Count);
            var seen = new HashSet<long>();
            foreach (var capture in list)
            {
                var id = EnsureCapture(capture);
                if (seen.Add(id)) ids.Add(id);
            }

            var key = SearchKey(prefix, filter);
            var searchId = searches.TryGetValue(key, out var existing) ? existing.Id : nextSearchId++;
            searches[key] = (searchId, cachedAt, ids);
            return Task.FromResult(BuildSnapshot(searchId, cachedAt, ids));
        }
    }

    public Task<StoredPage?> GetPageAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!captureIds.TryGetValue(Key(timestamp, original), out var id) || !pages.TryGetValue(id, out var page))
                return Task.FromResult<StoredPage?>(null);
            return Task.FromResult<StoredPage?>(includeHtml ? page : page with { HtmlGz = null });
        }
    }

    public Task SavePagesAsync(IReadOnlyList<NewPage> newPages, CancellationToken ct = default)
    {
        lock (gate)
        {
            saveBatchSizes.Add(newPages.Count);
            foreach (var p in newPages)
            {
                var id = EnsureCapture(p.Capture);
                pages[id] = new StoredPage(captures[id], p.HtmlGz, p.Parsed, p.FetchedAt);
            }
        }
        return Task.CompletedTask;
    }

    public Task<BackgroundOperation?> FindActiveOperationAsync(string prefix, string filter, OperationKind kind,
        CancellationToken ct = default)
    {
        lock (gate)
        {
            var key = OperationKey(prefix, filter, kind);
            return Task.FromResult(operations.TryGetValue(key, out var operation) && operation.IsActive
                ? operation
                : null);
        }
    }

    public Task<BackgroundOperation> CreateOrGetOperationAsync(string prefix, string filter, OperationKind kind,
        DateTimeOffset now, CancellationToken ct = default)
    {
        lock (gate)
        {
            var key = OperationKey(prefix, filter, kind);
            if (operations.TryGetValue(key, out var existing) && existing.IsActive)
                return Task.FromResult(existing);

            var operation = new BackgroundOperation(nextOperationId++, prefix, filter, kind,
                OperationState.Queued, now, now, 0, 0, 0);
            operations[key] = operation;
            return Task.FromResult(operation);
        }
    }

    public Task<BackgroundOperation?> GetOperationAsync(long id, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult(operations.Values.FirstOrDefault(operation => operation.Id == id));
    }

    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsAsync(bool activeOnly = false,
        CancellationToken ct = default)
    {
        lock (gate)
        {
            IReadOnlyList<BackgroundOperation> result = operations.Values
                .Where(operation => !activeOnly || operation.IsActive)
                .OrderByDescending(operation => operation.UpdatedAt)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task UpdateOperationAsync(BackgroundOperation operation, CancellationToken ct = default)
    {
        lock (gate) operations[OperationKey(operation.Prefix, operation.Filter, operation.Kind)] = operation;
        return Task.CompletedTask;
    }

    private long EnsureCapture(Capture capture)
    {
        var key = Key(capture.Timestamp, capture.Original);
        if (captureIds.TryGetValue(key, out var id)) return id;
        id = nextCaptureId++;
        captureIds[key] = id;
        captures[id] = capture;
        return id;
    }

    private SearchSnapshot BuildSnapshot(long searchId, DateTimeOffset cachedAt, List<long> ids)
    {
        var rows = ids
            .Select(id => new StoredCapture(id, captures[id], pages.ContainsKey(id)))
            .OrderByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(c => c.Id)
            .ToList();
        return new SearchSnapshot(searchId, cachedAt, rows);
    }

    private static string Key(string timestamp, string original) => timestamp + "|" + original;
    private static string SearchKey(string prefix, string filter) => filter + "|" + prefix;
    private static string OperationKey(string prefix, string filter, OperationKind kind) => $"{kind}|{filter}|{prefix}";
}
