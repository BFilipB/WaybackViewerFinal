using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Controllers;

/// Searched URLs: the list (keyset paginated), the per-URL capture page (offset paginated) and autocomplete.
/// Only the current page's rows are read from the store and rendered.
[Route("urls")]
public class UrlsController(IArchiveStore store, IMemoryCache cache, ILogger<UrlsController> log) : Controller
{
    public const int DefaultPageSize = 25;
    public const int MaxSuggestChars = 200;
    public const int SuggestLimit = UrlQueries.MaxSuggestions;
    private static readonly TimeSpan SuggestTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OptionsTtl = TimeSpan.FromSeconds(30);

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q = null, string? sort = null, string? after = null, long afterId = 0,
        int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        var normalizedSort = UrlSorts.Normalize(sort);
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (search is { Length: > 200 }) return BadRequest();
        if (after is { Length: > 300 }) return BadRequest();

        UrlListPage page;
        try
        {
            page = await store.GetUrlListAsync(new UrlListQuery(search, normalizedSort, after, afterId,
                Math.Clamp(pageSize, 1, 100)), ct);
        }
        catch (ArgumentException ex)
        {
            log.LogWarning(ex, "Rejected invalid URL list cursor.");
            return BadRequest();
        }

        var last = page.Items.Count > 0 ? page.Items[^1] : null;
        return View(new UrlListViewModel(page.Items, search, normalizedSort,
            last is null ? null : UrlSorts.CursorKey(normalizedSort, last), last?.Id ?? 0, page.HasMore, after is null));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, int page = 1, int pageSize = DefaultPageSize, string? mime = null,
        string? status = null, string? from = null, string? to = null, string? sort = null, string? dir = null,
        CancellationToken ct = default)
    {
        var summary = await store.GetUrlSummaryAsync(id, ct);
        if (summary is null) return NotFound();

        var fromTs = NormalizeDate(from, start: true);
        var toTs = NormalizeDate(to, start: false);
        if ((from is { Length: > 0 } && fromTs is null) || (to is { Length: > 0 } && toTs is null)) return BadRequest();
        var cleanMime = Clean(mime, 100);
        var cleanStatus = Clean(status, 10);

        var cleanSort = CaptureSorts.Normalize(sort);
        var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        var size = Math.Clamp(pageSize, 1, 100);
        var number = Math.Max(1, page);
        var result = await store.GetSearchCapturesPageAsync(id, new UrlCaptureFilter(cleanMime, cleanStatus, fromTs, toTs, cleanSort, descending),
            (number - 1) * size, size, ct);
        var operations = await store.GetOperationsForSearchAsync(summary.Prefix, summary.Filter, ct);
        var options = await GetOptionsAsync(id, ct);
        return View(new UrlDetailViewModel(summary, result.Items, result.Total, number, size, cleanMime, cleanStatus,
            from, to, operations, cleanSort, descending, options));
    }

    /// Autocomplete over every previously captured URL: matches anywhere in the URL (empty = all), most captures first, at most 200 rows, briefly cached (bounded).
    [HttpGet("suggest")]
    public async Task<IActionResult> Suggest(string? q, CancellationToken ct = default)
    {
        var term = (q ?? "").Trim();
        if (term.Length > MaxSuggestChars) return Json(Array.Empty<UrlSuggestion>());

        var key = "suggest|" + term.ToLowerInvariant();
        if (cache.TryGetValue(key, out IReadOnlyList<UrlSuggestion>? hit) && hit is not null) return Json(hit);

        var found = await store.SuggestUrlsAsync(term, SuggestLimit, ct);
        cache.Set(key, found, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = SuggestTtl });
        return Json(found);
    }

    /// Distinct MIME types / status codes for the filter dropdowns; briefly cached per search (bounded cache), null when the store cannot supply them.
    private async Task<UrlCaptureOptions?> GetOptionsAsync(long id, CancellationToken ct)
    {
        var key = "urlopts|" + id.ToString(CultureInfo.InvariantCulture);
        if (cache.TryGetValue(key, out UrlCaptureOptions? hit) && hit is not null) return hit;
        var found = await store.GetUrlCaptureOptionsAsync(id, ct);
        if (found is not null) cache.Set(key, found, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = OptionsTtl });
        return found;
    }

    private static string? Clean(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) || value.Length > max ? null : value.Trim();

    /// yyyy-MM-dd (date input) to a 14-digit Wayback timestamp; null when empty or invalid.
    private static string? NormalizeDate(string? value, bool start)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + (start ? "000000" : "235959");
    }
}
