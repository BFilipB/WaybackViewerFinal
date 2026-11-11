using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// Persistent store for capture lists and downloaded pages.
public interface IArchiveStore
{
    /// Creates the database / tables when they do not exist. Safe to call repeatedly.
    Task InitializeAsync(CancellationToken ct = default);

    /// The saved capture list for a search, newest capture first, or null when never saved.
    Task<SearchSnapshot?> GetSearchAsync(string prefix, string filter, CancellationToken ct = default);

    /// Loads only one page of a saved search plus its total capture count.
    Task<PagedSearchResult?> GetSearchPageAsync(string prefix, string filter, int offset, int limit,
        CancellationToken ct = default);

    /// Saves (or replaces) the capture list for a search and returns it. Captures and pages already stored are kept.
    Task<SearchSnapshot> SaveSearchAsync(string prefix, string filter, IReadOnlyList<Capture> captures,
        DateTimeOffset cachedAt, CancellationToken ct = default);

    /// One saved page, or null. includeHtml=false skips the (large) HTML column.
    Task<StoredPage?> GetPageAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default);

    /// Best-effort indexed lookup for search-text matches over saved page metadata/body text.
    Task<IReadOnlyList<long>> MatchCaptureIdsAsync(string query, CancellationToken ct = default);

    /// Popularity metadata for saved pages in a capture search, keyed by timestamp/original URL.
    Task<IReadOnlyDictionary<string, PageMetadata>> GetPopularityMetadataAsync(string prefix, string filter, CancellationToken ct = default);

    /// Inserts or replaces pages in a single round trip. Unknown captures are created on the fly.
    Task SavePagesAsync(IReadOnlyList<NewPage> pages, CancellationToken ct = default);

    /// Persists terminal preload outcomes (skipped / failed) in one round trip. Unknown captures are created on the fly.
    Task SaveOutcomesAsync(IReadOnlyList<CaptureOutcome> outcomes, CancellationToken ct = default);

    /// Skipped / failed outcomes of the captures in a search that have no saved page, keyed by capture key.
    Task<IReadOnlyDictionary<string, CaptureOutcome>> GetOutcomesAsync(string prefix, string filter, CancellationToken ct = default);

    /// Forgets the Failed outcomes of a search so those captures are pending again. Returns how many were reset.
    Task<int> ResetFailedAsync(string prefix, string filter, CancellationToken ct = default);

    /// Returns the active operation for this search, if one exists.
    Task<BackgroundOperation?> FindActiveOperationAsync(string prefix, string filter, OperationKind kind,
        CancellationToken ct = default);

    /// Creates an operation, or returns the existing active operation with the same key.
    Task<BackgroundOperation> CreateOrGetOperationAsync(string prefix, string filter, OperationKind kind,
        DateTimeOffset now, CancellationToken ct = default);

    Task<BackgroundOperation?> GetOperationAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<BackgroundOperation>> GetOperationsAsync(bool activeOnly = false,
        CancellationToken ct = default);

    Task UpdateOperationAsync(BackgroundOperation operation, CancellationToken ct = default);

    // ---- URL-scoped operation queries. The defaults scan GetOperationsAsync (fine for small in-memory stores);
    //      the SQL stores override them with indexed queries so a request never loads every operation.

    /// All operations (any kind/state) of one searched URL, newest first.
    async Task<IReadOnlyList<BackgroundOperation>> GetOperationsForSearchAsync(string prefix, string filter,
        CancellationToken ct = default) =>
        (await GetOperationsAsync(false, ct).ConfigureAwait(false))
            .Where(o => o.Prefix == prefix && o.Filter == filter).ToList();

    /// How many other searched URLs have at least one unfinished operation.
    async Task<int> CountOtherActiveSearchesAsync(string prefix, string filter, CancellationToken ct = default) =>
        (await GetOperationsAsync(true, ct).ConfigureAwait(false))
            .Where(o => !(o.Prefix == prefix && o.Filter == filter))
            .Select(o => new SearchKey(o.Prefix, o.Filter)).Distinct().Count();

    /// One page of searched URLs that have operations, most recently updated first.
    async Task<OperationSearchPage> ListOperationSearchesAsync(OperationListQuery query, CancellationToken ct = default)
    {
        var search = query.Search?.Trim();
		var groups = (await GetOperationsAsync(false, ct).ConfigureAwait(false))
			.Where(o => query.Kinds is null || query.Kinds.Contains(o.Kind))
			.Where(o => query.States is null || query.States.Contains(o.State))
			.Where(o => string.IsNullOrEmpty(search)
				|| o.Prefix.Contains(search, StringComparison.OrdinalIgnoreCase)
				|| o.Filter.Contains(search, StringComparison.OrdinalIgnoreCase))
			.GroupBy(o => new SearchKey(o.Prefix, o.Filter));
		IOrderedEnumerable<IGrouping<SearchKey, BackgroundOperation>> ordered = query.Sort switch
		{
			OperationSort.Url => query.Descending
				? groups.OrderByDescending(g => g.Key.Prefix, StringComparer.Ordinal).ThenByDescending(g => g.Key.Filter, StringComparer.Ordinal)
				: groups.OrderBy(g => g.Key.Prefix, StringComparer.Ordinal).ThenBy(g => g.Key.Filter, StringComparer.Ordinal),
			OperationSort.State => query.Descending
				? groups.OrderBy(g => g.Min(o => OperationStates.SortRank(o.State)))
				: groups.OrderByDescending(g => g.Min(o => OperationStates.SortRank(o.State))),
			_ => query.Descending
				? groups.OrderByDescending(g => g.Max(o => o.UpdatedAt))
				: groups.OrderBy(g => g.Max(o => o.UpdatedAt))
		};
		var all = ordered.ThenBy(g => g.Key.Prefix, StringComparer.Ordinal)
			.ThenBy(g => g.Key.Filter, StringComparer.Ordinal)
			.Select(g => g.Key).ToList();
        return new OperationSearchPage(all.Skip(Math.Max(0, query.Offset)).Take(Math.Max(0, query.Limit)).ToList(), all.Count);
    }

	// ---- searched URLs (summary table, autocomplete, per-URL captures) ----------------------------------------

	/// One keyset page of searched URLs (search text, sort, cursor).
	Task<UrlListPage> GetUrlListAsync(UrlListQuery query, CancellationToken ct = default) =>
		throw new NotSupportedException();

	Task<UrlSummary?> GetUrlSummaryAsync(long searchId, CancellationToken ct = default) =>
		throw new NotSupportedException();

	/// Searched URLs starting with the prefix, most captures first.
	Task<IReadOnlyList<UrlSuggestion>> SuggestUrlsAsync(string prefix, int limit, CancellationToken ct = default) =>
		throw new NotSupportedException();

	/// One page of a searched URL's captures (filtered, newest first); only that page is read.
	Task<CaptureRowsPage> GetSearchCapturesPageAsync(long searchId, UrlCaptureFilter filter, int offset, int limit,
		CancellationToken ct = default) =>
		throw new NotSupportedException();
    /// Distinct MIME types / status codes of one searched URL (bounded, in SQL). Null = not supported (callers fall back to a fixed list).
    Task<UrlCaptureOptions?> GetUrlCaptureOptionsAsync(long searchId, CancellationToken ct = default) =>
        Task.FromResult<UrlCaptureOptions?>(null);

    /// Saved pages of a search without a publish date, after the given capture id (bounded batch, in SQL). Drives the published-date backfill.
    Task<IReadOnlyList<StoredCapture>> GetPagesMissingPublishedAsync(string prefix, string filter, long afterId, int limit,
        CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCapture>>([]);
    /// Saved pages whose metadata was written by an older extractor version (bounded, keyset-paged).
    Task<IReadOnlyList<StoredCapture>> GetPagesWithStaleExtractorAsync(string prefix, string filter, int currentVersion, long afterId, int limit,
        CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCapture>>([]);
    /// Rewrites only the metadata rows of already-saved pages (no payload/text rewrite), in one transaction.
    Task UpdateMetadataAsync(IReadOnlyList<(Capture Capture, PageMetadata Metadata)> items, CancellationToken ct = default) => Task.CompletedTask;

    /// One page of the facet-filtered list plus facet counts, all computed in SQL. Null = not supported / free-text
    /// filter (the caller falls back to the in-memory list).
    Task<FilteredPage?> GetFilteredPageAsync(string prefix, string filter, CaptureFilter captureFilter, int page, int pageSize,
        string? contentTerm = null, CancellationToken ct = default) => Task.FromResult<FilteredPage?>(null);

	/// Newest captures of one URL inside a saved list (bounded, in SQL). Null = not supported / list not saved.
	Task<IReadOnlyList<StoredCapture>?> GetUrlHistoryAsync(string prefix, string filter, string original, int limit,
		CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCapture>?>(null);

	/// Like GetFilteredPageAsync but ranked/thresholded by popularity (metadata, else the archive signal), all in SQL.
    Task<FilteredPage?> GetPopularPageAsync(string prefix, string filter, CaptureFilter captureFilter, int page, int pageSize,
        string? contentTerm, PopularityQuery popularity, CancellationToken ct = default) => Task.FromResult<FilteredPage?>(null);

    /// Unsaved HTML captures of the filtered list, oldest first, after the cursor (one bounded batch per call).
    Task<IReadOnlyList<StoredCapture>> GetUnsavedHtmlBatchAsync(string prefix, string filter, CaptureFilter captureFilter,
        StoredCapture? after, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCapture>>([]);
    // ---- delete (default implementations so test fakes need not implement them) --------------------------------

    /// Counts and size of what a delete of this searched URL would remove.
    Task<DeletePreview> GetDeletePreviewAsync(string prefix, string filter, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// Removes up to batchSize preloaded pages (rows, outcomes and HTML files) that no other searched URL lists.
    /// Returns how many were removed; 0 means nothing is left.
    Task<int> DeletePagesBatchAsync(string prefix, string filter, int batchSize, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// Removes up to batchSize captures from this searched URL. Captures another searched URL still lists keep
    /// their rows and pages; the others are deleted completely. Returns how many were processed.
    Task<int> DeleteCapturesBatchAsync(string prefix, string filter, int batchSize, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// Removes the (now empty) searched URL itself.
    Task DeleteSearchAsync(string prefix, string filter, CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// Removes the operation rows of the given kinds for this searched URL.
    Task<int> DeleteOperationsAsync(string prefix, string filter, IReadOnlyCollection<OperationKind> kinds,
        CancellationToken ct = default) =>
        throw new NotSupportedException();

    /// All operations of the given searched URLs (the visible rows of the Operations page).
    async Task<IReadOnlyList<BackgroundOperation>> GetOperationsForSearchesAsync(IReadOnlyCollection<SearchKey> searches,
        CancellationToken ct = default)
    {
        if (searches.Count == 0) return [];
        var set = searches.ToHashSet();
        return (await GetOperationsAsync(false, ct).ConfigureAwait(false))
            .Where(o => set.Contains(new SearchKey(o.Prefix, o.Filter))).ToList();
    }}
