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
        string? type = null, string? unique = null, string? downloadAll = null, string? sort = null,
                string? popularityMetric = null, int minPopularity = 0, CancellationToken ct = default)
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
        var sortMode = NormalizeSortMode(sort);
        var popularityMode = NormalizePopularityMetric(popularityMetric);
        var popularityThreshold = popularityMode == "none" ? 0 : Math.Max(0, minPopularity);
        var popularityActive = sortMode != "newest" || popularityMode != "none";
        _downloadAllRequested = IsTrue(downloadAll);

        if (string.IsNullOrWhiteSpace(url))
            return View(new CaptureListViewModel("", activeFilter, [], null, PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch));

        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null)
            return View(new CaptureListViewModel(url.Trim(), activeFilter, [],
                "Enter a valid URL without spaces, for example example.com/blog/.", PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch,
                SortMode: sortMode, PopularityMetric: popularityMode, MinPopularity: popularityThreshold));

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
                if (popularityActive)
                    searched = await ApplyPopularityAsync(searched, prefix, activeFilter, sortMode, popularityMode, popularityThreshold, ct);
                total = searched.Count;
                totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                page = Math.Clamp(page, 1, totalPages);
                shown = mode == "server" ? searched.Skip((page - 1) * pageSize).Take(pageSize).ToList() : searched;
                QueuePendingPopularityDownloads(prefix, activeFilter);
                return View(WithPopularity(new CaptureListViewModel(prefix, activeFilter, shown, null,
                    result.FromDatabase, result.Snapshot.CachedAt, mode, page, pageSize, total, facets,
                    cf with { Text = ContentTerm(q) }, "content", groupSizes,
                    hiddenNotSaved, result.Refreshing, sortMode,
                    popularityMode, popularityThreshold)));
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
            if (popularityActive)
            {
                var basis = cf.IsActive ? facetCache.GetOrApply(result.Snapshot, cf).Items : everything;
                var ranked = await ApplyPopularityAsync(basis, prefix, activeFilter, sortMode, popularityMode, popularityThreshold, ct);
                total = ranked.Count;
                totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
                page = Math.Clamp(page, 1, totalPages);
                shown = mode == "server" ? ranked.Skip((page - 1) * pageSize).Take(pageSize).ToList() : ranked;
            }
            if (!cf.IsActive && !popularityActive && mode == "server")
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
            QueuePendingPopularityDownloads(prefix, activeFilter);
            return View(WithPopularity(new CaptureListViewModel(prefix, activeFilter, shown, null,
                result.FromDatabase, result.Snapshot.CachedAt, mode, page, pageSize, total, facets, cf, modeOfSearch,
                groupSizes, hiddenNotSaved, result.Refreshing, sortMode, popularityMode, popularityThreshold)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return View(new CaptureListViewModel(prefix, activeFilter, [], ex.Message, PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch,
                SortMode: sortMode, PopularityMetric: popularityMode, MinPopularity: popularityThreshold));
        }
    }

    private static string NormalizeSortMode(string? v) =>
        v?.Trim().ToLowerInvariant() is "popular" or "engaged" or "commented" or "liked" or "viewed" or "score"
            ? v.Trim().ToLowerInvariant() : "newest";

    private static string NormalizePopularityMetric(string? v) =>
        v?.Trim().ToLowerInvariant() is "views" or "likes" or "comments" or "engaged" or "score"
            ? v.Trim().ToLowerInvariant() : "none";

    private Dictionary<string, PageMetadata>? _popularityCache;
    private readonly HashSet<string> _popularityMisses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredCapture> _popularityPending = new(StringComparer.Ordinal);
    private List<PopularityPreset>? _popularityPresets;
    private List<int>? _popularityHistogram;
    private int _popularityExcluded;

    private async Task<IReadOnlyDictionary<string, PageMetadata>> LoadPopularityMetadataAsync(IReadOnlyList<StoredCapture> captures,
        string prefix, string filter, CancellationToken ct)
    {
        _popularityCache ??= new Dictionary<string, PageMetadata>(
            await store.GetPopularityMetadataAsync(prefix, filter, ct).ConfigureAwait(false), StringComparer.Ordinal);
        var metadata = _popularityCache;

        foreach (var capture in captures.Select(c => c.Capture).DistinctBy(c => c.Key))
        {
            if (metadata.ContainsKey(capture.Key) || !_popularityMisses.Add(capture.Key)) continue;
            ct.ThrowIfCancellationRequested();
            var pageMetadata = await pages.GetPopularityMetadataAsync(capture.Timestamp, capture.Original, ct).ConfigureAwait(false);
            if (pageMetadata is not null) metadata[capture.Key] = pageMetadata;
        }

        foreach (var c in captures)
            if (c.Capture.IsHtml && !c.Ready && !metadata.ContainsKey(c.Capture.Key)) _popularityPending[c.Capture.Key] = c;

        return metadata;
    }

    private async Task<List<StoredCapture>> ApplyPopularityAsync(IReadOnlyList<StoredCapture> captures, string prefix,
        string filter, string sortMode, string metric, int threshold, CancellationToken ct)
    {
        var metadata = await LoadPopularityMetadataAsync(captures, prefix, filter, ct).ConfigureAwait(false);
        metadata = ArchiveSignals.Fill(captures, metadata);
        IEnumerable<StoredCapture> items = captures;
        if (metric != "none")
        {
            _popularityPresets = BuildPresets(captures, metadata, metric);
            _popularityHistogram = BuildHistogram(captures, metadata, metric);
            if (threshold > 0)
            {
                var kept = captures.Where(c => GetPopularityMetric(metadata, c, metric) >= threshold).ToList();
                _popularityExcluded += captures.Count - kept.Count;
                items = kept;
            }
        }
        if (sortMode == "newest")
            return items.OrderByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal).ThenByDescending(c => c.Id).ToList();
        return items.OrderByDescending(c => GetPopularityMetric(metadata, c, sortMode))
            .ThenByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(c => c.Id)
            .ToList();
    }

    private static long GetPopularityMetric(IReadOnlyDictionary<string, PageMetadata> metadata, StoredCapture capture, string metric)
    {
        if (!metadata.TryGetValue(capture.Capture.Key, out var m)) return 0;
        return metric switch
        {
            "views" or "viewed" => m.Views ?? 0,
            "likes" or "liked" => m.Likes ?? 0,
            "comments" or "commented" => m.Comments ?? 0,
            "score" => m.Score ?? 0,
            "engaged" => m.EffectiveEngagementScore,
            _ => Math.Max((m.Views ?? 0) + (m.Likes ?? 0) + (m.Comments ?? 0) + (m.Shares ?? 0) + (m.Score ?? 0), m.EffectiveEngagementScore)
        };
    }

    private static List<PopularityPreset> BuildPresets(IReadOnlyList<StoredCapture> captures,
        IReadOnlyDictionary<string, PageMetadata> metadata, string metric)
    {
        var values = captures.Select(c => GetPopularityMetric(metadata, c, metric)).OrderByDescending(v => v).ToArray();
        var presets = new List<PopularityPreset>();
        if (values.Length == 0) return presets;
        foreach (var percent in new[] { 10, 25, 50 })
        {
            var index = Math.Clamp((int)Math.Ceiling(values.Length * percent / 100.0) - 1, 0, values.Length - 1);
            var value = values[index];
            if (value > 0 && (presets.Count == 0 || presets[^1].Value != value))
                presets.Add(new PopularityPreset(percent, (int)Math.Min(value, int.MaxValue)));
        }
        return presets;
    }

    private void QueuePendingPopularityDownloads(string prefix, string filter)
    {
        if (_popularityPending.Count == 0) return;
        if (Request.Headers.TryGetValue("Sec-Purpose", out var purpose) &&
            purpose.ToString().Contains("prefetch", StringComparison.OrdinalIgnoreCase)) return;
        var queue = _popularityPending.Values.ToList();
        _ = Task.Run(() =>
        {
            try { preload.EnqueueView(queue, prefix, filter, true); }
            catch (Exception ex) { log.LogWarning(ex, "Queueing popularity downloads failed"); }
        });
    }

    private bool _downloadAllRequested;

    private CaptureListViewModel WithPopularity(CaptureListViewModel model) =>
        _popularityCache is null ? model with { DownloadAll = _downloadAllRequested } : model with
        {
            DownloadAll = _downloadAllRequested,
            Popularity = _popularityCache,
            PopularityExcluded = _popularityExcluded,
            PopularityPending = _popularityPending.Count,
            Presets = _popularityPresets,
            Histogram = _popularityHistogram
        };

    private static List<int> BuildHistogram(IReadOnlyList<StoredCapture> captures,
        IReadOnlyDictionary<string, PageMetadata> metadata, string metric)
    {
        const int buckets = 10;
        var values = captures.Select(c => GetPopularityMetric(metadata, c, metric)).ToArray();
        var bins = new int[buckets];
        if (values.Length == 0) return [.. bins];
        var max = values.Max();
        foreach (var v in values)
            bins[max <= 0 ? 0 : (int)Math.Min(buckets - 1, v * buckets / (max + 1))]++;
        return [.. bins];
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
        string? searchMode = null, string? type = null, string? unique = null, string? sort = null,
        string? popularityMetric = null, int minPopularity = 0, CancellationToken ct = default)
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

        var exportSort = NormalizeSortMode(sort);
        var exportMetric = NormalizePopularityMetric(popularityMetric);
        if (exportSort != "newest" || exportMetric != "none")
            captures = await ApplyPopularityAsync(captures, prefix, activeFilter, exportSort, exportMetric,
                exportMetric == "none" ? 0 : Math.Max(0, minPopularity), ct);

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

    /// URLs that already have a capture list in the database (most recently used first), for the search box suggestions.
    public async Task<IActionResult> KnownUrls(CancellationToken ct = default)
    {
        var operations = await store.GetOperationsAsync(activeOnly: false, ct);
        var urls = operations
            .Where(o => o.Kind == OperationKind.CaptureLoad && o.Completed > 0 && !string.IsNullOrWhiteSpace(o.Prefix))
            .OrderByDescending(o => o.UpdatedAt)
            .Select(o => o.Prefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        return Json(urls);
    }

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

    // Only server pagination exists: the HTML table always contains a single page of rows. Older links that still say
    // pagination=client are accepted and served as server pages.
    private static string NormalizePaginationMode(string? value) => "server";

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
