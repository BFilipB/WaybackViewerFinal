using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Controllers;

public class HomeController(CaptureCatalog catalog, PageService pages, PreloadCoordinator preload, IArchiveStore store,
    FacetCache facetCache, IOptions<PreloadOptions> preloadOptions, ILogger<HomeController> log) : Controller
{
    private readonly Dictionary<string, (DateTimeOffset At, List<StoredCapture> Items)> textSearchCache =
        new(StringComparer.Ordinal);

    public async Task<IActionResult> Index(string? url, string? filter, bool refresh = false,
        string? pagination = null, int page = 1, int pageSize = 50, string? year = null, string? category = null,
        string? status = null, string? section = null, string? q = null, string? searchMode = null,
        string? type = null, string? unique = null, string? downloadAll = null, CancellationToken ct = default)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["request"] = "index",
            ["url"] = url,
            ["filter"] = filter,
            ["page"] = page,
            ["pageSize"] = pageSize
        });

        var activeFilter = UrlInput.NormalizeFilter(filter);
        var modeOfSearch = NormalizeSearchMode(searchMode);
        var mode = NormalizePaginationMode(pagination);
        page = Math.Max(1, page);
        pageSize = NormalizePageSize(pageSize);

        if (string.IsNullOrWhiteSpace(url))
            return View(new CaptureListViewModel("", activeFilter, [], null, PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch));

        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null)
            return View(new CaptureListViewModel(url.Trim(), activeFilter, [],
                "Enter a valid URL without spaces, for example example.com/blog/.", PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch));

        try
        {
            var result = await catalog.GetAsync(prefix, activeFilter, refresh, ct);
            var hiddenNotSaved = 0;
            var everything = result.Snapshot.Captures;
            var downloadEverything = IsTrue(downloadAll) ||
                                     string.Equals(preloadOptions.Value.Scope, "all", StringComparison.OrdinalIgnoreCase);

            // Download in the background only what the current view needs (filters, one per thread ...).
            // The rest stays one click away ("download the rest") instead of costing minutes of traffic.
            void QueueDownloads(IReadOnlyList<StoredCapture> view)
            {
                var queue = downloadEverything ? everything : view;
                if (!downloadEverything)
                    hiddenNotSaved = everything.Count(c => c.Capture.IsHtml && !c.Ready) - view.Count(c => c.Capture.IsHtml && !c.Ready);

                // A browser that only guesses where you will click next (prefetch) must not start downloads.
                var prefetch = Request.Headers.TryGetValue("Sec-Purpose", out var purpose) &&
                               purpose.ToString().Contains("prefetch", StringComparison.OrdinalIgnoreCase);
                if (prefetch || !queue.Any(c => c.Capture.IsHtml && !c.Ready)) return;

                var boost = !downloadEverything;
                // Off the request: queueing tens of thousands of downloads must never delay drawing the page.
                _ = Task.Run(() =>
                {
                    try { preload.EnqueueView(queue, prefix, activeFilter, boost); }
                    catch (Exception ex) { log.LogWarning(ex, "Queueing downloads failed"); }
                });
            }

            var total = result.Snapshot.Captures.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            IReadOnlyList<StoredCapture> shown = result.Snapshot.Captures;
            var facets = facetCache.GetOrBuild(result.Snapshot);
            var cf = new CaptureFilter(Clean(year), Clean(category), Clean(status), Clean(section), Clean(q),
                UrlClassifier.NormalizeKind(type), IsTrue(unique));
            var fullText = IsContentSearch(modeOfSearch, q);
            IReadOnlyDictionary<string, int>? groupSizes = null;
            if (fullText)
            {
                var searched = await ApplyFullTextFilterAsync(result.Snapshot.Captures, ContentTerm(q), ct);
                QueueDownloads(everything);   // a content search can only match saved pages, so keep saving all of them
                var structural = cf with { Text = null };
                if (structural.IsActive)
                {
                    if (cf.Unique) groupSizes = CaptureFacets.GroupSizes(searched, structural);
                    searched = CaptureFacets.Apply(searched, structural);
                }
                total = searched.Count;
                totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                page = Math.Clamp(page, 1, totalPages);
                shown = mode == "server" ? searched.Skip((page - 1) * pageSize).Take(pageSize).ToList() : searched;
                return View(new CaptureListViewModel(prefix, activeFilter, shown, null,
                    result.FromDatabase, result.Snapshot.CachedAt, mode, page, pageSize, total, facets,
                    cf with { Text = ContentTerm(q) }, SearchMode: modeOfSearch, GroupSizes: groupSizes,
                    HiddenNotSaved: hiddenNotSaved, Refreshing: result.Refreshing));
            }
            if (cf.IsActive)
            {
                var filteredView = facetCache.GetOrApply(result.Snapshot, cf);   // remembered per list and filter
                var filtered = filteredView.Items;
                groupSizes = filteredView.GroupSizes;
                QueueDownloads(filtered);
                total = filtered.Count;
                totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                page = Math.Clamp(page, 1, totalPages);
                shown = mode == "server" ? filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList() : filtered;
            }
            else QueueDownloads(everything);
            if (!cf.IsActive && mode == "server")
            {
                var requestedPage = page;
                var paged = await store.GetSearchPageAsync(prefix, activeFilter, 0, 0, ct);
                if (paged is not null)
                {
                    total = paged.TotalCount;
                    totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                    page = Math.Clamp(requestedPage, 1, totalPages);
                    paged = await store.GetSearchPageAsync(prefix, activeFilter, (page - 1) * pageSize, pageSize, ct);
                    shown = paged?.Captures ?? [];
                }
            }
            return View(new CaptureListViewModel(prefix, activeFilter, shown, null,
                result.FromDatabase, result.Snapshot.CachedAt, mode, page, pageSize, total, facets, cf, modeOfSearch,
                groupSizes, hiddenNotSaved, result.Refreshing));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return View(new CaptureListViewModel(prefix, activeFilter, [], ex.Message, PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch));
        }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool IsTrue(string? v) =>
        v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
                          v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static bool IsContentSearch(string mode, string? query) =>
        mode == "content" || (!string.IsNullOrWhiteSpace(query) && query.Trim().StartsWith("text:", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSearchMode(string? value) =>
        string.Equals(value, "content", StringComparison.OrdinalIgnoreCase) ? "content" : "url";

    private static string ContentTerm(string? q)
    {
        var raw = q?.Trim() ?? "";
        return raw.StartsWith("text:", StringComparison.OrdinalIgnoreCase) ? raw[5..].Trim() : raw;
    }

    private async Task<List<StoredCapture>> ApplyFullTextFilterAsync(IReadOnlyList<StoredCapture> captures, string term, CancellationToken ct)
    {
        if (term.Length == 0) return captures.ToList();
        var key = SearchTextMatcher.CacheKey(term);

        if (textSearchCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            return cached.Items;

        var results = new List<StoredCapture>();
        foreach (var item in captures)
        {
            ct.ThrowIfCancellationRequested();
            if (SearchTextMatcher.Matches(term, item.Capture.Original))
            {
                results.Add(item);
                continue;
            }

            try
            {
                var page = await pages.GetAsync(item.Capture.Timestamp, item.Capture.Original, includeHtml: false, ct);
                if (page is null) continue;
                if (SearchTextMatcher.MatchesAny(term,
                    page.Parsed.Title,
                    page.Parsed.Description,
                    page.Parsed.Text))
                    results.Add(item);
            }
            catch
            {
                // keep search resilient; skip captures that cannot be loaded
            }
        }

        // If nothing matched in saved/local parsed text yet, do one archive-backed pass via the existing text endpoint pipeline.
        if (results.Count == 0)
        {
            foreach (var item in captures)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var page = await pages.GetAsync(item.Capture.Timestamp, item.Capture.Original, includeHtml: false, ct);
                    if (page is null) continue;
                    if (SearchTextMatcher.MatchesAny(term,
                        page.Parsed.Title,
                        page.Parsed.Description,
                        page.Parsed.Text))
                        results.Add(item);
                }
                catch
                {
                    // keep search resilient; skip captures that cannot be loaded
                }
            }
        }

        results = results
            .OrderByDescending(s => s.Capture.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(s => s.Id)
            .ToList();
        textSearchCache[key] = (DateTimeOffset.UtcNow, results);
        return results;
    }

    /// Full capture set for the current query/filter/facets, independent of current page rendering.
    public async Task<IActionResult> CaptureSet(string url, string? filter,
        string? year = null, string? category = null, string? status = null, string? section = null, string? q = null,
        string? searchMode = null, string? type = null, string? unique = null, CancellationToken ct = default)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null) return BadRequest();
        var activeFilter = UrlInput.NormalizeFilter(filter);

        var mode = NormalizeSearchMode(searchMode);
        var result = await catalog.GetAsync(prefix, activeFilter, false, ct);
        IReadOnlyList<StoredCapture> captures = result.Snapshot.Captures;
        var cf = new CaptureFilter(Clean(year), Clean(category), Clean(status), Clean(section), Clean(q),
            UrlClassifier.NormalizeKind(type), IsTrue(unique));
        if (IsContentSearch(mode, q))
        {
            captures = await ApplyFullTextFilterAsync(result.Snapshot.Captures, ContentTerm(q), ct);
            var structural = cf with { Text = null };
            if (structural.IsActive) captures = CaptureFacets.Apply(captures, structural);
        }
        else if (cf.IsActive)
            captures = facetCache.GetOrApply(result.Snapshot, cf).Items;

        return Json(captures.Select(c => new
        {
            ts = c.Capture.Timestamp,
            url = c.Capture.Original,
            ready = c.Ready   // archive / frame links are derived in the browser (about 60% smaller payload)
        }));
    }

    /// Every known capture of one URL inside a loaded search (for the inspector panel).
    public async Task<IActionResult> History(string url, string? filter, string original, CancellationToken ct)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null || !UrlInput.IsValidCaptureUrl(original)) return BadRequest();

        var result = await catalog.GetAsync(prefix, UrlInput.NormalizeFilter(filter), false, ct);
        return Json(result.Snapshot.Captures
            .Where(c => c.Capture.Original == original)
            .OrderByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal)
            .Take(60)
            .Select(c => new { ts = c.Capture.Timestamp, status = c.Capture.StatusCode, ready = c.Ready }));
    }

    /// Visible text of one capture (database first, downloaded on demand otherwise).
    public async Task<IActionResult> Text(string ts, string url, CancellationToken ct)
    {
        if (!UrlInput.IsValidTimestamp(ts) || !UrlInput.IsValidCaptureUrl(url)) return BadRequest();

        var page = await pages.GetAsync(ts, url, includeHtml: false, ct);
        if (page is not null) Response.Headers.CacheControl = "private,max-age=3600"; // a capture at a fixed timestamp never changes
        return Json(page is null
            ? new PageText(null, null, "", "The capture could not be loaded from the Wayback Machine.")
            : new PageText(page.Parsed.Title, page.Parsed.Description, page.Parsed.Text));
    }

    public IActionResult PreloadStatus() => Json(preload.GetStatus());

    public async Task<IActionResult> OperationStatus(CancellationToken ct = default) =>
        Json(await store.GetOperationsAsync(activeOnly: false, ct));

    public async Task<IActionResult> WorkflowStatus(string url, string? filter, CancellationToken ct = default)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null) return BadRequest();
        var activeFilter = UrlInput.NormalizeFilter(filter);

        var operations = await store.GetOperationsAsync(activeOnly: false, ct);
        var related = operations
            .Where(o => o.Prefix == prefix && o.Filter == activeFilter)
            .OrderByDescending(o => o.UpdatedAt)
            .ToList();

        BackgroundOperation? capture = related
            .FirstOrDefault(o => o.Kind is OperationKind.CaptureLoad or OperationKind.WaybackRefresh);
        BackgroundOperation? preloadOp = related
            .FirstOrDefault(o => o.Kind == OperationKind.PagePreload);

        var preloadStatus = preload.GetStatus();
        var preloadJob = preloadStatus.Jobs?.FirstOrDefault(j => j.Prefix == prefix && j.Filter == activeFilter);

        var captureBar = capture is null
            ? new WorkflowBar("Capture list", "Idle", 0, 0, 0, false)
            : new WorkflowBar(
                capture.Kind == OperationKind.WaybackRefresh ? "Wayback refresh" : "Capture list",
                capture.State.ToString(),
                capture.Completed,
                capture.Total,
                capture.Total > 0 ? Math.Round(capture.Completed * 100.0 / Math.Max(1, capture.Total), 1) : 0,
                capture.IsActive,
                capture.Error);

        var preloadCompleted = preloadJob?.Completed ?? preloadOp?.Completed ?? 0;
        var preloadTotal = preloadJob?.Total ?? preloadOp?.Total ?? 0;
        var preloadState = preloadJob?.State.ToString() ?? preloadOp?.State.ToString() ?? (preloadStatus.Running ? "Running" : "Idle");
        var preloadError = preloadOp?.Error;
        var preloadBar = new WorkflowBar(
            "Page preload",
            preloadState,
            preloadCompleted,
            preloadTotal,
            preloadTotal > 0 ? Math.Round(preloadCompleted * 100.0 / Math.Max(1, preloadTotal), 1) : 0,
            preloadState is "Queued" or "Running" or "Paused" or "Stopping" or "Interrupted",
            preloadError);

        var timeline = related
            .Select(o => new WorkflowEvent(o.Kind.ToString(), o.State.ToString(), o.UpdatedAt, o.Completed, o.Total, o.Error))
            .Take(24)
            .ToList();

        var message = captureBar.Active || preloadBar.Active
            ? "Workflow active"
            : timeline.Count > 0 ? "Workflow idle" : "No workflow yet";

        return Json(new WorkflowStatusView(message, captureBar, preloadBar, timeline));
    }

    [HttpPost]
    public async Task<IActionResult> StartSearch([FromForm] string? url, [FromForm] string? filter,
        [FromForm] bool refresh = false, [FromForm] string? pagination = null, [FromForm] int pageSize = 50)
    {
        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null) return BadRequest("Enter a valid URL without spaces.");
        var activeFilter = UrlInput.NormalizeFilter(filter);
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["request"] = "start-search",
            ["prefix"] = prefix,
            ["filter"] = activeFilter,
            ["refresh"] = refresh
        });
        var mode = NormalizePaginationMode(pagination);
        var searchMode = NormalizeSearchMode(null);
        pageSize = NormalizePageSize(pageSize);
        var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
        if (!refresh && await catalog.HasFreshSnapshotAsync(prefix, activeFilter))
            return Json(new { operationId = (long?)null, cached = true, prefix, filter = activeFilter, refresh, pagination = mode, pageSize, searchMode });
        var operation = await store.CreateOrGetOperationAsync(prefix, activeFilter, kind, DateTimeOffset.UtcNow);
        _ = RunSearchAsync(prefix, activeFilter, refresh);
        return Json(new { operationId = operation.Id, prefix, filter = activeFilter, refresh, pagination = mode, pageSize, searchMode });
    }

    [HttpPost]
    public async Task<IActionResult> Pause([FromQuery] long? operationId = null, [FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        if (operationId is not null) return await SetOperationState(operationId.Value, OperationState.Paused);
        preload.Pause(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    private async Task<IActionResult> SetOperationState(long operationId, OperationState state)
    {
        var operation = await store.GetOperationAsync(operationId);
        if (operation is null) return NotFound();
        if (operation.Kind == OperationKind.PagePreload)
        {
            if (state == OperationState.Paused) preload.Pause(operationId);
            else if (state == OperationState.Running) preload.Resume(operationId);
            else if (state == OperationState.Stopped) preload.Stop(operationId);
        }
        else if (operation.IsActive)
        {
            await store.UpdateOperationAsync(operation with { State = state, UpdatedAt = DateTimeOffset.UtcNow });
        }
        return NoContent();
    }

    private async Task RunSearchAsync(string prefix, string filter, bool refresh)
    {
        using var scope = log.BeginScope(new Dictionary<string, object?>
        {
            ["request"] = "run-search",
            ["prefix"] = prefix,
            ["filter"] = filter,
            ["refresh"] = refresh
        });
        try
        {
            var result = await catalog.GetAsync(prefix, filter, refresh).ConfigureAwait(false);
            if (result.Snapshot.Captures.Any(c => c.Capture.IsHtml && !c.Ready))
                preload.Enqueue(result.Snapshot.Captures, prefix, filter);
        }
        catch (Exception ex)
        {
            var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
            var operation = await store.FindActiveOperationAsync(prefix, filter, kind).ConfigureAwait(false);
            if (operation is not null)
                await store.UpdateOperationAsync(operation with
                {
                    State = OperationState.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = ex.Message
                }).ConfigureAwait(false);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Resume([FromQuery] long? operationId = null, [FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        if (operationId is not null) return await SetOperationState(operationId.Value, OperationState.Running);
        preload.Resume(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Stop([FromQuery] long? operationId = null, [FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        if (operationId is not null) return await SetOperationState(operationId.Value, OperationState.Stopped);
        preload.Stop(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    private static string? NormalizeOptionalUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : UrlInput.NormalizePrefix(url);

    private static string NormalizePaginationMode(string? value) =>
        string.Equals(value, "client", StringComparison.OrdinalIgnoreCase) ? "client" : "server";

    private static int NormalizePageSize(int value) => value switch
    {
        < 10 => 10,
        > 200 => 200,
        _ => value
    };

    /// Moves the given captures ("timestamp|url") to the front of the background download queue.
    [HttpPost]
    public IActionResult Prioritize([FromBody] string[] keys)
    {
        if (keys is null || keys.Length > 200) return BadRequest();
        preload.Boost(keys);
        return NoContent();
    }

    /// The archived HTML, served from the database with a base tag so relative assets load from Wayback.
    public async Task<IActionResult> Frame(string ts, string url, CancellationToken ct)
    {
        if (!UrlInput.IsValidTimestamp(ts) || !UrlInput.IsValidCaptureUrl(url)) return BadRequest();

        var page = await pages.GetAsync(ts, url, includeHtml: true, ct);
        if (page?.HtmlGz is null) return Redirect(ArchiveUrls.Frame(ts, url));

        var html = FrameHtml.InjectBase(HtmlCodec.Decompress(page.HtmlGz), ArchiveUrls.Frame(ts, url));
        Response.Headers.CacheControl = "private,max-age=86400"; // a capture at a fixed timestamp never changes
        Response.Headers.ContentSecurityPolicy = "sandbox allow-scripts allow-popups";
        return Content(html, "text/html", Encoding.UTF8);
    }
}
