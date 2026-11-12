using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Controllers;

public class HomeController(CaptureCatalog catalog, PageService pages, PreloadCoordinator preload, IArchiveStore store,
    CaptureLoadRunner loads,
    FacetCache facetCache, IOptions<PreloadOptions> preloadOptions, ILogger<HomeController> log) : Controller
{
    private readonly Dictionary<string, (DateTimeOffset At, List<StoredCapture> Items)> textSearchCache =
        new(StringComparer.Ordinal);

    public async Task<IActionResult> Index(string? url, string? filter, bool refresh = false,
        string? pagination = null, int page = 1, int pageSize = 50, string? year = null, string? category = null,
        string? status = null, string? section = null, string? q = null, string? searchMode = null,
        string? type = null, string? unique = null, string? downloadAll = null, string? sort = null,
                string? popularityMetric = null, int minPopularity = 0, bool includeEstimates = false, int maxPopularity = 0, CancellationToken ct = default)
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
        _maxPopularity = popularityMode == "none" ? 0 : Math.Max(0, maxPopularity);
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
            // Fast path: a saved, fresh list is filtered, counted and paged in SQL; only this page's rows are read.
            var contentSearch = IsContentSearch(modeOfSearch, q);
            if (mode == "server")
            {
                // A content search matches saved pages only (the CaptureSearch index), so it runs as one more SQL filter.
                var sqlFilter = new CaptureFilter(Clean(year), Clean(category), Clean(status), Clean(section),
                    contentSearch ? null : Clean(q), UrlClassifier.NormalizeKind(type), IsTrue(unique));
                var fresh = await catalog.TryGetFreshStateAsync(prefix, activeFilter, refresh, ct);
                var sqlTerm = contentSearch ? ContentTerm(q) : null;
                var queryStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var sqlPage = fresh is null ? null : popularityActive
                    ? await store.GetPopularPageAsync(prefix, activeFilter, sqlFilter, page, pageSize, sqlTerm,
                        new PopularityQuery(sortMode, popularityMode, popularityThreshold, includeEstimates, popularityMode == "none" ? 0 : Math.Max(0, maxPopularity)), ct)
                    : await store.GetFilteredPageAsync(prefix, activeFilter, sqlFilter, page, pageSize, sqlTerm, ct);
                if (fresh is not null)
                    log.LogInformation("list.query prefix={Prefix} filter={Filter} sort={Sort} metric={Metric} minPop={MinPop} year={Year} category={Category} status={Status} section={Section} type={Type} unique={Unique} text={HasText} content={Content} page={Page} pageSize={PageSize} ms={Ms:F0} total={Total} pageRows={Rows} unsavedInView={UnsavedView} unsavedTotal={UnsavedTotal} popularityPending={Pending} excluded={Excluded}",
                        prefix, activeFilter, sortMode, popularityMode, popularityThreshold, sqlFilter.Year, sqlFilter.Category, sqlFilter.Status, sqlFilter.Section,
                        sqlFilter.Kind, sqlFilter.Unique, !string.IsNullOrEmpty(sqlFilter.Text), contentSearch, page, pageSize,
                        System.Diagnostics.Stopwatch.GetElapsedTime(queryStart).TotalMilliseconds, sqlPage?.Total, sqlPage?.Captures.Count,
                        sqlPage?.UnsavedHtmlInView, sqlPage?.UnsavedHtmlTotal, sqlPage?.PopularityPending, sqlPage?.PopularityExcluded);
                if (sqlPage is { Total: 0 })
                    log.LogInformation("ui.empty_result prefix={Prefix} filter={Filter} sort={Sort} year={Year} category={Category} status={Status} section={Section} type={Type} unique={Unique} text={HasText} content={Content} minPop={MinPop}",
                        prefix, activeFilter, sortMode, sqlFilter.Year is not null, sqlFilter.Category is not null, sqlFilter.Status is not null, sqlFilter.Section is not null,
                        sqlFilter.Kind, sqlFilter.Unique, !string.IsNullOrEmpty(sqlFilter.Text), contentSearch, popularityThreshold);
                else if (page >= 10)
                    log.LogInformation("ui.deep_page prefix={Prefix} page={Page} pageSize={PageSize} total={Total} sort={Sort}", prefix, page, pageSize, sqlPage?.Total, sortMode);
                if (fresh is not null && sqlPage is not null)
                {
                    if (sortMode == "published") pages.StartPublishedBackfill(prefix, activeFilter);
                    pages.StartStaleReprocess(prefix, activeFilter);
                    // A content search can only match saved pages, so it keeps saving all of them.
                    var everythingRequested = contentSearch || IsTrue(downloadAll) ||
                        string.Equals(preloadOptions.Value.Scope, "all", StringComparison.OrdinalIgnoreCase);
                    var prefetching = Request.Headers.TryGetValue("Sec-Purpose", out var sqlPurpose) &&
                        sqlPurpose.ToString().Contains("prefetch", StringComparison.OrdinalIgnoreCase);
                    var needed = everythingRequested ? sqlPage.UnsavedHtmlTotal : sqlPage.UnsavedHtmlInView;
                    if (!prefetching && needed > 0)
                    {
                        var queueFilter = everythingRequested ? new CaptureFilter() : sqlFilter;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await preload.EnqueueFilteredAsync(prefix, activeFilter, queueFilter, sqlPage.HtmlTotal,
                                    sqlPage.UnsavedHtmlTotal, needed, boost: !everythingRequested);
                            }
                            catch (Exception ex) { log.LogWarning(ex, "Queueing downloads failed"); }
                        });
                    }
                    var hidden = everythingRequested ? 0 : Math.Max(0, sqlPage.UnsavedHtmlTotal - sqlPage.UnsavedHtmlInView);
                    var sqlPages = Math.Max(1, (int)Math.Ceiling(sqlPage.Total / (double)pageSize));
                    if (popularityActive)
                    {
                        _popularityCache = new Dictionary<string, PageMetadata>(sqlPage.Popularity ?? new Dictionary<string, PageMetadata>(), StringComparer.Ordinal);
                        _popularityExcluded = sqlPage.PopularityExcluded;
                        _popularityPresets = sqlPage.Presets is null ? null : [.. sqlPage.Presets];
                        _popularityHistogram = sqlPage.Histogram is null ? null : [.. sqlPage.Histogram];
                        _coverage = (sqlPage.CoverageMeasured, sqlPage.CoverageTotal); _metricCoverage = sqlPage.MetricCoverage;
                    }
                    return View(WithPopularity(new CaptureListViewModel(prefix, activeFilter, sqlPage.Captures, null,
                        fresh.FromDatabase, sqlPage.CachedAt, mode, Math.Min(sqlPage.Page, sqlPages), pageSize, sqlPage.Total,
                        sqlPage.Facets, contentSearch ? sqlFilter with { Text = ContentTerm(q) } : sqlFilter, modeOfSearch, sqlPage.GroupSizes, hidden, false, sortMode,
                        popularityMode, popularityThreshold)) with { IncludeEstimates = includeEstimates, MaxPopularity = popularityMode == "none" ? 0 : Math.Max(0, maxPopularity), PopularityPending = popularityActive ? sqlPage.PopularityPending : 0 });
                }
            }
            log.LogInformation("list.fallback prefix={Prefix} filter={Filter} reason={Reason} sort={Sort} metric={Metric} page={Page}", prefix, activeFilter, mode != "server" ? "pagination_not_server" : "no_fresh_sql_snapshot", sortMode, popularityMode, page);
            CatalogResult result;
            // Bounded wait: a slow CDX download must not hold the page open for minutes. The shared in-flight
            // download keeps running and the load continues as a persisted CaptureLoad operation.
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var catalogWait = TimeSpan.FromMilliseconds(Math.Max(1, HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetValue("Performance:CatalogWaitMs", 15000)));
                wait.CancelAfter(catalogWait);
                try { result = await catalog.GetAsync(prefix, activeFilter, refresh, wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    log.LogInformation("list.load_deferred prefix={Prefix} filter={Filter} waitMs={Wait}", prefix, activeFilter, (long)catalogWait.TotalMilliseconds);
                    await store.CreateOrGetOperationAsync(prefix, activeFilter, refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
                    loads.Start(prefix, activeFilter, refresh);
                    return View(new CaptureListViewModel(prefix, activeFilter, [],
                        "The capture list is still loading from the Wayback Machine. It continues in the background; progress is shown in the workflow timeline. Reload in a moment.",
                        PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch,
                        SortMode: sortMode, PopularityMetric: popularityMode, MinPopularity: popularityThreshold));
                }
            }
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
                _ = Task.Run(async () =>
                {
                    try { await preload.EnqueueViewAsync(queue, prefix, activeFilter, boost); }
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
                    searched = await ApplyPopularityAsync(searched, prefix, activeFilter, sortMode, popularityMode, popularityThreshold, ct, includeEstimates, popularityMode == "none" ? 0 : Math.Max(0, maxPopularity));
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
                var ranked = await ApplyPopularityAsync(basis, prefix, activeFilter, sortMode, popularityMode, popularityThreshold, ct, includeEstimates, popularityMode == "none" ? 0 : Math.Max(0, maxPopularity));
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
            log.LogWarning(ex, "Loading captures for {Prefix} failed", prefix);
            return View(new CaptureListViewModel(prefix, activeFilter, [], CaptureCatalog.DescribeFailure(ex), PaginationMode: mode, PageSize: pageSize, SearchMode: modeOfSearch,
                SortMode: sortMode, PopularityMetric: popularityMode, MinPopularity: popularityThreshold));
        }
    }

    private static string NormalizeSortMode(string? v) =>
        v?.Trim().ToLowerInvariant() is "published" or "popular" or "engaged" or "commented" or "liked" or "viewed" or "score"
            or "least-popular" or "least-engaged" or "least-commented" or "least-liked" or "least-viewed" or "least-score"
            ? v.Trim().ToLowerInvariant() : "newest";

    private static string NormalizePopularityMetric(string? v) =>
        v?.Trim().ToLowerInvariant() is "views" or "likes" or "comments" or "engaged" or "score"
            ? v.Trim().ToLowerInvariant() : "none";

    private Dictionary<string, PageMetadata>? _popularityCache;
    private readonly HashSet<string> _popularityMisses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredCapture> _popularityPending = new(StringComparer.Ordinal);
    private List<PopularityPreset>? _popularityPresets;
    private List<int>? _popularityHistogram;
    private (int Measured, int Total) _coverage;
    private int _maxPopularity;
    private IReadOnlyDictionary<string, int>? _metricCoverage;
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
        string filter, string sortMode, string metric, int threshold, CancellationToken ct, bool includeEstimates = false, int maxThreshold = 0)
    {
        var metadata = await LoadPopularityMetadataAsync(captures, prefix, filter, ct).ConfigureAwait(false);
        metadata = ArchiveSignals.Fill(captures, metadata);
        IEnumerable<StoredCapture> items = captures;
        if (metric != "none")
        {
            _popularityPresets = BuildPresets(captures, metadata, metric, includeEstimates);
            _popularityHistogram = BuildHistogram(captures, metadata, metric, includeEstimates);
            _coverage = (captures.Count(c => metadata.TryGetValue(c.Capture.Key, out var m) && m.Tier >= 2), captures.Count);
            _metricCoverage = new Dictionary<string, int> { ["views"] = CountOf(m => m.Views), ["likes"] = CountOf(m => m.Likes), ["comments"] = CountOf(m => m.Comments), ["score"] = CountOf(m => m.Score) };
            int CountOf(Func<PageMetadata, long?> pick) => captures.Count(c => metadata.TryGetValue(c.Capture.Key, out var pm) && pm.Tier >= 2 && pick(pm).HasValue);
            if (threshold > 0 || maxThreshold > 0)
            {
                var kept = captures.Where(c => Eligible(metadata, c, includeEstimates) && GetPopularityMetric(metadata, c, metric) >= threshold && (maxThreshold <= 0 || GetPopularityMetric(metadata, c, metric) <= maxThreshold)).ToList();
                _popularityExcluded += captures.Count - kept.Count;
                items = kept;
            }
        }
        if (sortMode == "newest")
            return items.OrderByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal).ThenByDescending(c => c.Id).ToList();
        if (sortMode == "published")
        {
            DateTimeOffset? PublishedOf(StoredCapture c) => metadata.TryGetValue(c.Capture.Key, out var m) ? m.PublishedAt : null;
            return items.OrderBy(c => PublishedOf(c).HasValue ? 0 : 1)
                .ThenByDescending(c => PublishedOf(c))
                .ThenByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal)
                .ThenByDescending(c => c.Id)
                .ToList();
        }
        var least = sortMode.StartsWith("least-", StringComparison.Ordinal);
        var sortMetric = least ? sortMode["least-".Length..] : sortMode;
        int TierOf(StoredCapture c) => metadata.TryGetValue(c.Capture.Key, out var tm) ? tm.Tier : 0;
        var pct = Percentiles(items, c => SortLevel(metadata, c, sortMetric) * 10 + TierOf(c), c => SortValue(metadata, c, sortMetric));
        double Pk(StoredCapture c) => pct[c.Id] * (metadata.TryGetValue(c.Capture.Key, out var m) ? 0.5 + 0.5 * m.Confidence : 1.0);
        var tiered = items.OrderByDescending(c => SortLevel(metadata, c, sortMetric)).ThenByDescending(TierOf);
        return (least ? tiered.ThenBy(Pk).ThenBy(c => WeightedKey(metadata, c, sortMetric)) : tiered.ThenByDescending(Pk).ThenByDescending(c => WeightedKey(metadata, c, sortMetric)))
            .ThenByDescending(c => c.Capture.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(c => c.Id)
            .ToList();
    }

    /// Unknown is not zero (S7): single-metric sorts rank rows lacking that metric after every row that has it, for most and least.
    /// Views have no real count on many sites (reddit): rows with other counts form a middle level ranked by an estimated proxy.
    private static int SortLevel(IReadOnlyDictionary<string, PageMetadata> metadata, StoredCapture c, string metric)
    {
        if (!metadata.TryGetValue(c.Capture.Key, out var m)) return metric is "views" or "viewed" or "likes" or "liked" or "comments" or "commented" or "score" ? 0 : 1;
        return metric switch
        {
            "views" or "viewed" => m.Views.HasValue ? 2 : m.Likes.HasValue || m.Comments.HasValue || m.Score.HasValue || m.Shares.HasValue ? 1 : 0,
            "likes" or "liked" => m.Likes.HasValue ? 1 : 0,
            "comments" or "commented" => m.Comments.HasValue ? 1 : 0,
            "score" => m.Score.HasValue ? 1 : 0,
            _ => 1
        };
    }

    /// Value ranked by a sort: the metric itself, except views without a real count use the engagement score as an estimated proxy.
    private static long SortValue(IReadOnlyDictionary<string, PageMetadata> metadata, StoredCapture c, string metric) =>
        metric is "views" or "viewed" && metadata.TryGetValue(c.Capture.Key, out var m) && !m.Views.HasValue
            ? m.EffectiveEngagementScore
            : GetPopularityMetric(metadata, c, metric);
    /// Within-tier percentile (SQL CUME_DIST: share of rows with a value <= this one), per capture id.
    private static Dictionary<long, double> Percentiles(IEnumerable<StoredCapture> items, Func<StoredCapture, int> tier, Func<StoredCapture, long> value)
    {
        var result = new Dictionary<long, double>();
        foreach (var group in items.GroupBy(tier))
        {
            var sorted = group.Select(value).OrderBy(v => v).ToArray();
            foreach (var c in group)
            {
                var v = value(c);
                var lo = 0; var hi = sorted.Length;
                while (lo < hi) { var mid = (lo + hi) / 2; if (sorted[mid] <= v) lo = mid + 1; else hi = mid; }
                result[c.Id] = (double)lo / sorted.Length;
            }
        }
        return result;
    }

    /// Sort key inside a tier: metric * (0.5 + 0.5 * confidence), so equally popular pages rank by how sure the extraction is.
    private static double WeightedKey(IReadOnlyDictionary<string, PageMetadata> metadata, StoredCapture c, string metric) =>
        SortValue(metadata, c, metric) * (metadata.TryGetValue(c.Capture.Key, out var m) ? 0.5 + 0.5 * m.Confidence : 1.0);

    /// Exact filters count measured pages only; estimates and archive-signal rows join when requested.
    private static bool Eligible(IReadOnlyDictionary<string, PageMetadata> metadata, StoredCapture capture, bool includeEstimates) =>
        includeEstimates || (metadata.TryGetValue(capture.Capture.Key, out var m) && m.Tier >= 2);

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
        IReadOnlyDictionary<string, PageMetadata> metadata, string metric, bool includeEstimates)
    {
        var values = captures.Where(c => Eligible(metadata, c, includeEstimates)).Select(c => GetPopularityMetric(metadata, c, metric)).OrderByDescending(v => v).ToArray();
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
        _ = Task.Run(async () =>
        {
            try { await preload.EnqueueViewAsync(queue, prefix, filter, true); }
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
            Histogram = _popularityHistogram,
            CoverageMeasured = _coverage.Measured,
            CoverageTotal = _coverage.Total,
            MaxPopularity = _maxPopularity,
            MetricCoverage = _metricCoverage
        };

    private static List<int> BuildHistogram(IReadOnlyList<StoredCapture> captures,
        IReadOnlyDictionary<string, PageMetadata> metadata, string metric, bool includeEstimates)
    {
        const int buckets = 10;
        var values = captures.Where(c => Eligible(metadata, c, includeEstimates)).Select(c => GetPopularityMetric(metadata, c, metric)).ToArray();
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // keep search resilient; skip captures that cannot be loaded
                log.LogDebug(ex, "Full-text search skipped {Key}", item.Capture.Key);
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // keep search resilient; skip captures that cannot be loaded
                    log.LogDebug(ex, "Full-text search skipped {Key}", item.Capture.Key);
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
    /// max (0 = unlimited) caps the list for browser consumers such as the slideshow.
    public async Task<IActionResult> CaptureSet(string url, string? filter,
        string? year = null, string? category = null, string? status = null, string? section = null, string? q = null,
        string? searchMode = null, string? type = null, string? unique = null, string? sort = null,
        string? popularityMetric = null, int minPopularity = 0, bool includeEstimates = false, int maxPopularity = 0,
        int max = 0, CancellationToken ct = default)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null) return BadRequest();
        var query = new CaptureSetQuery(prefix, UrlInput.NormalizeFilter(filter), year, category, status, section, q,
            searchMode, type, unique, sort, popularityMetric, minPopularity, includeEstimates, maxPopularity);

        Response.ContentType = "application/json; charset=utf-8";
        await using var json = new System.Text.Json.Utf8JsonWriter(Response.Body);
        json.WriteStartArray();
        var written = 0;
        await foreach (var c in EnumerateCaptureSetAsync(query, ct))
        {
            if (max > 0 && written >= max) break;
            json.WriteStartObject();
            json.WriteString("ts", c.Capture.Timestamp);
            json.WriteString("url", c.Capture.Original);
            json.WriteBoolean("ready", c.Ready);   // archive / frame links are derived in the browser
            json.WriteEndObject();
            if (++written % 2000 == 0) await json.FlushAsync(ct);
        }
        json.WriteEndArray();
        await json.FlushAsync(ct);
        return new EmptyResult();
    }

    /// Text/metadata export of the full filtered set, written to the response one capture at a time:
    /// neither the server nor the browser holds the whole file (the browser just downloads a stream).
    public async Task<IActionResult> Export(string url, string? filter, string? format = null,
        string? year = null, string? category = null, string? status = null, string? section = null, string? q = null,
        string? searchMode = null, string? type = null, string? unique = null, string? sort = null,
        string? popularityMetric = null, int minPopularity = 0, bool includeEstimates = false, int maxPopularity = 0,
        CancellationToken ct = default)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null) return BadRequest();
        var query = new CaptureSetQuery(prefix, UrlInput.NormalizeFilter(filter), year, category, status, section, q,
            searchMode, type, unique, sort, popularityMetric, minPopularity, includeEstimates, maxPopularity);
        var fmt = ExportWriter.NormalizeFormat(format);

        Response.ContentType = ExportWriter.ContentType(fmt);
        Response.Headers.ContentDisposition = "attachment; filename=\"wayback-export-all." + fmt + "\"";
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false), 16 * 1024);
        await writer.WriteAsync(ExportWriter.Header(fmt).AsMemory(), ct);
        var first = true;
        await foreach (var c in EnumerateCaptureSetAsync(query, ct))
        {
            var ts = c.Capture.Timestamp;
            var original = c.Capture.Original;
            var archive = "https://web.archive.org/web/" + ts + "/" + original;
            ExportRecord record;
            try
            {
                var page = await pages.GetAsync(ts, original, includeHtml: false, ct);
                record = page is null
                    ? new ExportRecord(original, archive, ts, null, null, "", "The capture could not be loaded from the Wayback Machine.")
                    : new ExportRecord(original, archive, ts, page.Parsed.Title, page.Parsed.Description, page.Parsed.Text, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Export: text of {Timestamp} {Url} failed", ts, original);
                record = new ExportRecord(original, archive, ts, null, null, "", "Text extraction failed");
            }
            await writer.WriteAsync(ExportWriter.Row(fmt, record, first).AsMemory(), ct);
            await writer.FlushAsync(ct);
            first = false;
        }
        await writer.WriteAsync(ExportWriter.Footer(fmt).AsMemory(), ct);
        await writer.FlushAsync(ct);
        return new EmptyResult();
    }

    private sealed record CaptureSetQuery(string Prefix, string ActiveFilter, string? Year, string? Category, string? Status,
        string? Section, string? Q, string? SearchMode, string? Type, string? Unique, string? Sort, string? PopularityMetric,
        int MinPopularity, bool IncludeEstimates, int MaxPopularity);

    /// Streams the filtered capture set from SQL one page at a time (bounded memory); falls back to the catalog.
    private async IAsyncEnumerable<StoredCapture> EnumerateCaptureSetAsync(CaptureSetQuery qy,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var prefix = qy.Prefix;
        var activeFilter = qy.ActiveFilter;
        var mode = NormalizeSearchMode(qy.SearchMode);
        var exportContent = IsContentSearch(mode, qy.Q);
        var exportFilter = new CaptureFilter(Clean(qy.Year), Clean(qy.Category), Clean(qy.Status), Clean(qy.Section),
            exportContent ? null : Clean(qy.Q), UrlClassifier.NormalizeKind(qy.Type), IsTrue(qy.Unique));
        var streamSort = NormalizeSortMode(qy.Sort);
        var streamMetric = NormalizePopularityMetric(qy.PopularityMetric);
        var minPopularity = streamMetric == "none" ? 0 : Math.Max(0, qy.MinPopularity);
        var maxPopularity = streamMetric == "none" ? 0 : Math.Max(0, qy.MaxPopularity);
        var streamPopularity = streamSort != "newest" || streamMetric != "none"
            ? new PopularityQuery(streamSort, streamMetric, minPopularity, qy.IncludeEstimates, maxPopularity) : null;

        var urlText = exportFilter.Text;   // matched per row in C# (SearchTextMatcher); SQL pages stay bounded
        exportFilter = exportFilter with { Text = null };
        if (await catalog.TryGetFreshStateAsync(prefix, activeFilter, false, ct) is not null)
        {
            const int exportPage = 2000;
            var term = exportContent ? ContentTerm(qy.Q) : null;
            async Task<FilteredPage?> ReadExportPage(int number) => streamPopularity is null
                ? await store.GetFilteredPageAsync(prefix, activeFilter, exportFilter, number, exportPage, term, ct)
                : await store.GetPopularPageAsync(prefix, activeFilter, exportFilter, number, exportPage, term, streamPopularity, ct);

            var current = await ReadExportPage(1);
            if (current is not null)
            {
                for (var pageNumber = 1; ; )
                {
                    foreach (var c in current.Captures)
                        if (urlText is null || SearchTextMatcher.Matches(urlText, c.Capture.Original)) yield return c;
                    if (pageNumber * exportPage >= current.Total || current.Captures.Count == 0) yield break;
                    pageNumber++;
                    current = await ReadExportPage(pageNumber);
                    if (current is null) yield break;
                }
            }
        }

        var result = await catalog.GetAsync(prefix, activeFilter, false, ct);
        IReadOnlyList<StoredCapture> captures = result.Snapshot.Captures;
        var cf = new CaptureFilter(Clean(qy.Year), Clean(qy.Category), Clean(qy.Status), Clean(qy.Section), Clean(qy.Q),
            UrlClassifier.NormalizeKind(qy.Type), IsTrue(qy.Unique));
        if (exportContent)
        {
            captures = await ApplyFullTextFilterAsync(result.Snapshot.Captures, ContentTerm(qy.Q), ct);
            var structural = cf with { Text = null };
            if (structural.IsActive) captures = CaptureFacets.Apply(captures, structural);
        }
        else if (cf.IsActive)
            captures = facetCache.GetOrApply(result.Snapshot, cf).Items;

        if (streamSort != "newest" || streamMetric != "none")
            captures = await ApplyPopularityAsync(captures, prefix, activeFilter, streamSort, streamMetric,
                minPopularity, ct, qy.IncludeEstimates, maxPopularity);

        foreach (var c in captures) yield return c;
    }

    /// Every known capture of one URL inside a loaded search (for the inspector panel).
    public async Task<IActionResult> History(string url, string? filter, string original, CancellationToken ct)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null || !UrlInput.IsValidCaptureUrl(original)) return BadRequest();

        var historyFilter = UrlInput.NormalizeFilter(filter);
        var rows = await store.GetUrlHistoryAsync(prefix, historyFilter, original, 60, ct);
        if (rows is not null)
            return Json(rows.Select(c => new { ts = c.Capture.Timestamp, status = c.Capture.StatusCode, ready = c.Ready }));

        var result = await catalog.GetAsync(prefix, historyFilter, false, ct);
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

    /// Operations of one searched URL (Home shows only the current URL). Without a URL: only unfinished operations.
    public async Task<IActionResult> OperationStatus(string? url = null, string? filter = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return Json(await store.GetOperationsAsync(activeOnly: true, ct));
        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null) return BadRequest();
        return Json(await store.GetOperationsForSearchAsync(prefix, UrlInput.NormalizeFilter(filter), ct));
    }

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
        var statusStart = System.Diagnostics.Stopwatch.GetTimestamp();

        var related = (await store.GetOperationsForSearchAsync(prefix, activeFilter, ct))
            .OrderByDescending(o => o.UpdatedAt)
            .ToList();
        var otherUrls = await store.CountOtherActiveSearchesAsync(prefix, activeFilter, ct);
        preload.SetPriority(prefix, activeFilter);   // the URL open on Home gets a larger share of the workers

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
            preloadOp?.IsActive ?? preloadState is "Queued" or "Running" or "Paused" or "Interrupted",
            preloadError);

        var timeline = related
            .Select(o => new WorkflowEvent(o.Kind.ToString(), o.State.ToString(), o.UpdatedAt, o.Completed, o.Total, o.Error))
            .Take(24)
            .ToList();

        var message = captureBar.Active || preloadBar.Active
            ? "Workflow active"
            : timeline.Count > 0 ? "Workflow idle" : "No workflow yet";

        var statusMs = System.Diagnostics.Stopwatch.GetElapsedTime(statusStart).TotalMilliseconds;
        if (statusMs >= 200)
            log.LogWarning("workflow.slow prefix={Prefix} filter={Filter} ms={Ms:F0} operations={Ops}", prefix, activeFilter, statusMs, related.Count);
        return Json(new WorkflowStatusView(message, captureBar, preloadBar, timeline, otherUrls));
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
            { log.LogInformation("search.start prefix={Prefix} filter={Filter} outcome=cached", prefix, activeFilter); return Json(new { operationId = (long?)null, cached = true, prefix, filter = activeFilter, refresh, pagination = mode, pageSize, searchMode }); }
        var operation = await store.CreateOrGetOperationAsync(prefix, activeFilter, kind, DateTimeOffset.UtcNow);
        loads.Start(prefix, activeFilter, refresh);
        log.LogInformation("search.start prefix={Prefix} filter={Filter} outcome=started kind={Kind} operationId={Id}", prefix, activeFilter, kind, operation.Id);
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
        }
        else if (OperationStates.CanTransition(operation.State, state))
        {
            await store.UpdateOperationAsync(operation with { State = state, UpdatedAt = DateTimeOffset.UtcNow });
            if (state == OperationState.Running)
                loads.Start(operation.Prefix, operation.Filter, operation.Kind == OperationKind.WaybackRefresh);
        }
        else
        {
            log.LogInformation("Ignored {State} for operation {Id} in state {Current}", state, operationId, operation.State);
            return Conflict();
        }
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Resume([FromQuery] long? operationId = null, [FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        if (operationId is not null) return await SetOperationState(operationId.Value, OperationState.Running);
        preload.Resume(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    /// Makes the failed captures of a search pending again and queues them for download.
    [HttpPost]
    public async Task<IActionResult> RetryFailed([FromQuery] string? url, [FromQuery] string? filter, CancellationToken ct = default)
    {
        var prefix = UrlInput.NormalizePrefix(url ?? "");
        if (prefix is null) return BadRequest();
        var reset = await preload.RetryFailedAsync(prefix, UrlInput.NormalizeFilter(filter), ct);
        return Json(new { reset });
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
    public async Task<IActionResult> Frame(string ts, string url, bool scripts = false, CancellationToken ct = default)
    {
        if (!UrlInput.IsValidTimestamp(ts) || !UrlInput.IsValidCaptureUrl(url)) return BadRequest();

        // A stored capture never changes: answer a revalidation with 304 without loading or decompressing the HTML.
        var etag = $"\"{ts}{(scripts ? "s" : "")}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16]}\"";
        if (Request.Headers.IfNoneMatch.ToString() == etag && await pages.IsStoredAsync(ts, url, ct))
        {
            Response.Headers.ETag = etag;
            return StatusCode(StatusCodes.Status304NotModified);
        }

        var page = await pages.GetAsync(ts, url, includeHtml: true, ct);
        if (page?.HtmlGz is null) return Redirect(ArchiveUrls.Frame(ts, url));
        Response.Headers.ETag = etag;

        var html = FrameHtml.InjectBase(HtmlCodec.Decompress(page.HtmlGz), ArchiveUrls.Frame(ts, url));
        Response.Headers.CacheControl = "private,max-age=86400"; // a capture at a fixed timestamp never changes
        Response.Headers.ContentSecurityPolicy = scripts ? "sandbox allow-scripts allow-popups" : "sandbox allow-popups";
        return Content(html, "text/html", Encoding.UTF8);
    }
}

