using WaybackViewer.Models;

namespace WaybackViewer.Storage;

public sealed partial class InMemoryArchiveStore
{
    private UrlSummary SummaryLocked(string key, (long Id, DateTimeOffset CachedAt, List<long> CaptureIds) s)
    {
        var bar = key.IndexOf('|', StringComparison.Ordinal);
        var stamps = s.CaptureIds.Select(id => captures[id].Timestamp).ToList();
        return new UrlSummary(s.Id, key[(bar + 1)..], key[..bar], s.CaptureIds.Count,
            stamps.Count == 0 ? null : stamps.Min(StringComparer.Ordinal), stamps.Count == 0 ? null : stamps.Max(StringComparer.Ordinal),
            s.CaptureIds.Count(pages.ContainsKey), s.CachedAt);
    }

    public Task<UrlListPage> GetUrlListAsync(UrlListQuery query, CancellationToken ct = default)
    {
        var sort = UrlSorts.Normalize(query.Sort);
        List<UrlSummary> all;
        lock (gate) all = searches.Select(kv => SummaryLocked(kv.Key, kv.Value)).ToList();

        IEnumerable<UrlSummary> items = all;
        if (!string.IsNullOrWhiteSpace(query.Search))
            items = items.Where(u => u.Prefix.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase));

        items = sort switch
        {
            UrlSorts.Alpha => items.OrderBy(u => u.Prefix, StringComparer.Ordinal).ThenBy(u => u.Id),
            UrlSorts.Recent => items.OrderByDescending(u => u.LastRefresh.ToUnixTimeMilliseconds()).ThenByDescending(u => u.Id),
            _ => items.OrderByDescending(u => u.CaptureCount).ThenByDescending(u => u.Id)
        };

        if (query.AfterKey is not null)
        {
            if (sort == UrlSorts.Alpha)
                items = items.Where(u => string.CompareOrdinal(u.Prefix, query.AfterKey) > 0 ||
                                         (u.Prefix == query.AfterKey && u.Id > query.AfterId));
            else
            {
                if (!long.TryParse(query.AfterKey, out var key)) throw new ArgumentException("Invalid cursor.", nameof(query));
                long Of(UrlSummary u) => sort == UrlSorts.Recent ? u.LastRefresh.ToUnixTimeMilliseconds() : u.CaptureCount;
                items = items.Where(u => Of(u) < key || (Of(u) == key && u.Id < query.AfterId));
            }
        }

        var limit = Math.Clamp(query.Limit, 1, UrlQueries.MaxLimit);
        var page = items.Take(limit + 1).ToList();
        var more = page.Count > limit;
        if (more) page.RemoveAt(page.Count - 1);
        return Task.FromResult(new UrlListPage(page, more));
    }

    public Task<UrlSummary?> GetUrlSummaryAsync(long searchId, CancellationToken ct = default)
    {
        lock (gate)
        {
            foreach (var kv in searches)
                if (kv.Value.Id == searchId) return Task.FromResult<UrlSummary?>(SummaryLocked(kv.Key, kv.Value));
            return Task.FromResult<UrlSummary?>(null);
        }
    }

    public Task<IReadOnlyList<UrlSuggestion>> SuggestUrlsAsync(string prefix, int limit, CancellationToken ct = default)
    {
        List<UrlSummary> all;
        lock (gate) all = searches.Select(kv => SummaryLocked(kv.Key, kv.Value)).ToList();
        IReadOnlyList<UrlSuggestion> result = all
            .Where(u => u.Prefix.Contains(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(u => u.Prefix, StringComparer.Ordinal)
            .Select(g => new UrlSuggestion(g.Key, g.Max(u => u.CaptureCount)))
            .OrderByDescending(s => s.Captures).ThenBy(s => s.Prefix, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, UrlQueries.MaxSuggestions)).ToList();
        return Task.FromResult(result);
    }

    public Task<CaptureRowsPage> GetSearchCapturesPageAsync(long searchId, UrlCaptureFilter filter, int offset, int limit,
        CancellationToken ct = default)
    {
        lock (gate)
        {
            var search = searches.Values.FirstOrDefault(s => s.Id == searchId);
            if (search.CaptureIds is null) return Task.FromResult(new CaptureRowsPage([], 0));
            var rows = search.CaptureIds
                .Select(id => (Id: id, Capture: captures[id]))
                .Where(x => filter.Mime is null || x.Capture.MimeType == filter.Mime)
                .Where(x => filter.Status is null || x.Capture.StatusCode == filter.Status)
                .Where(x => filter.FromTimestamp is null || string.CompareOrdinal(x.Capture.Timestamp, filter.FromTimestamp) >= 0)
                .Where(x => filter.ToTimestamp is null || string.CompareOrdinal(x.Capture.Timestamp, filter.ToTimestamp) <= 0)
                .OrderByDescending(x => x.Capture.Timestamp, StringComparer.Ordinal).ThenByDescending(x => x.Id)
                .ToList();
            var slice = rows.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, UrlQueries.MaxLimit))
                .Select(x => new StoredCapture(x.Id, x.Capture, pages.ContainsKey(x.Id))).ToList();
            return Task.FromResult(new CaptureRowsPage(slice, rows.Count));
        }
    }
}
