using Microsoft.AspNetCore.Mvc;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Controllers;

/// The Operations page: every searched URL with its background work, server-side paginated.
[Route("operations")]
public class OperationsController(IArchiveStore store, PreloadCoordinator preload, OperationControl control, DeleteRunner deleter,
    ILogger<OperationsController> log) : Controller
{
    public const int DefaultPageSize = 25;
    public const int MaxBulk = 200;
	public const int MaxSearchLength = 200;

    private static readonly Dictionary<string, OperationState[]> StateFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["active"] = [OperationState.Queued, OperationState.Running, OperationState.Interrupted],
        ["paused"] = [OperationState.Paused, OperationState.Stopping, OperationState.Stopped],
        ["completed"] = [OperationState.Completed],
        ["failures"] = [OperationState.CompletedWithFailures, OperationState.Failed]
    };

    private static readonly Dictionary<string, OperationKind[]> TypeFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["captureload"] = [OperationKind.CaptureLoad, OperationKind.WaybackRefresh],
        ["preload"] = [OperationKind.PagePreload]
    };

    [HttpGet("")]
    public async Task<IActionResult> Index(string? state = null, string? type = null, int page = 1, int pageSize = DefaultPageSize,
        string? q = null, string? sort = null, string? dir = null, CancellationToken ct = default)
    {
        var model = await LoadAsync(state, type, page, pageSize, ct, q, sort, dir);
        return View(model);
    }

    /// Status of the visible rows only (the client sends the keys it shows).
    [HttpPost("status")]
    public async Task<IActionResult> Status([FromBody] OperationSelection selection, CancellationToken ct = default)
    {
        var keys = ParseKeys(selection);
        if (keys is null) return BadRequest();
        var ops = await store.GetOperationsForSearchesAsync(keys, ct);
        return Json(BuildRows(keys, ops));
    }

    [HttpPost("pause")]
    public Task<IActionResult> Pause([FromBody] OperationSelection selection, CancellationToken ct = default) =>
        ApplyAsync(selection, (k, c) => control.PauseAsync(k, c), ct);

    [HttpPost("resume")]
    public Task<IActionResult> Resume([FromBody] OperationSelection selection, CancellationToken ct = default) =>
        ApplyAsync(selection, (k, c) => control.ResumeAsync(k, c), ct);

    [HttpPost("retry-failed")]
    public Task<IActionResult> RetryFailed([FromBody] OperationSelection selection, CancellationToken ct = default) =>
        ApplyAsync(selection, (k, c) => control.RetryFailedAsync(k, c), ct);

    [HttpPost("refresh")]
    public Task<IActionResult> Refresh([FromBody] OperationSelection selection, CancellationToken ct = default) =>
        ApplyAsync(selection, (k, c) => control.RefreshAsync(k, c), ct);

    /// What a delete would remove (counts and size), shown in the confirmation dialog.
    [HttpPost("delete-preview")]
    public async Task<IActionResult> DeletePreview([FromBody] DeleteRequest request, CancellationToken ct = default)
    {
        var parsed = ParseDeleteKey(request);
        if (parsed is not { } key) return BadRequest();
        return Json(await deleter.PreviewAsync(key.Prefix, key.Filter, ct));
    }

    /// Starts a background delete of one searched URL ("pages" keeps the capture list, "all" removes everything).
    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest request, CancellationToken ct = default)
    {
        var parsed = ParseDeleteKey(request);
        if (parsed is not { } key) return BadRequest();
        DeleteLevel level;
        if (string.Equals(request.Level, "pages", StringComparison.OrdinalIgnoreCase)) level = DeleteLevel.Pages;
        else if (string.Equals(request.Level, "all", StringComparison.OrdinalIgnoreCase)) level = DeleteLevel.All;
        else return BadRequest();

        var result = await deleter.BeginAsync(key.Prefix, key.Filter, level, ct);
        return result switch
        {
            DeleteStartResult.Started => Accepted(new { started = true }),
            DeleteStartResult.AlreadyDeleting => Conflict(new { error = "A delete of this URL is already running." }),
            _ => Conflict(new { error = "A capture list load of this URL is still running. Pause it and try again." })
        };
    }

    private static SearchKey? ParseDeleteKey(DeleteRequest? request)
    {
        var prefix = UrlInput.NormalizePrefix(request?.Key?.Prefix);
        return prefix is null ? null : new SearchKey(prefix, UrlInput.NormalizeFilter(request!.Key!.Filter));
    }

    private async Task<IActionResult> ApplyAsync(OperationSelection? selection, Func<SearchKey, CancellationToken, Task> action,
        CancellationToken ct)
    {
        if (selection is null) return BadRequest();
        IReadOnlyList<SearchKey>? keys;
        if (selection.All)
        {
            // "Pause all" etc.: walk the searched URLs page by page so memory stays bounded.
            var count = 0;
            for (var offset = 0; ; offset += MaxBulk)
            {
                var page = await store.ListOperationSearchesAsync(new OperationListQuery(null, null, offset, MaxBulk), ct);
                foreach (var key in page.Searches) { await action(key, ct); count++; }
                if (page.Searches.Count < MaxBulk) break;
            }
            log.LogInformation("Bulk operation action applied to {Count} searched URLs", count);
            return Json(new { applied = count });
        }

        keys = ParseKeys(selection);
        if (keys is null) return BadRequest();
        foreach (var key in keys) await action(key, ct);
        return Json(new { applied = keys.Count });
    }

    private static IReadOnlyList<SearchKey>? ParseKeys(OperationSelection? selection)
    {
        if (selection?.Keys is null || selection.Keys.Count > MaxBulk) return null;
        var result = new List<SearchKey>(selection.Keys.Count);
        foreach (var k in selection.Keys)
        {
            var prefix = UrlInput.NormalizePrefix(k?.Prefix);
            if (prefix is null) return null;
            result.Add(new SearchKey(prefix, UrlInput.NormalizeFilter(k!.Filter)));
        }
        return result.Distinct().ToList();
    }

    internal async Task<OperationsPageModel> LoadAsync(string? state, string? type, int page, int pageSize, CancellationToken ct,
        string? search = null, string? sort = null, string? dir = null)
    {
        search = search?.Trim();
        if (string.IsNullOrEmpty(search)) search = null;
        else if (search.Length > MaxSearchLength) search = search[..MaxSearchLength];
        var order = Enum.TryParse<OperationSort>(sort, true, out var parsedSort) && Enum.IsDefined(parsedSort) ? parsedSort : OperationSort.Updated;
        var descending = !string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase);
        pageSize = Math.Clamp(pageSize, 10, 100);
        page = Math.Max(1, page);
        var states = state is not null && StateFilters.TryGetValue(state, out var s) ? s : null;
        var kinds = type is not null && TypeFilters.TryGetValue(type, out var t) ? t : null;

        var result = await store.ListOperationSearchesAsync(new OperationListQuery(kinds, states, (page - 1) * pageSize, pageSize, search, order, descending), ct);
        var ops = await store.GetOperationsForSearchesAsync(result.Searches, ct);
        // Bulk "all" actions ignore search/filters, so the page shows how many URLs they really affect.
        var allTotal = search is null && states is null && kinds is null ? result.Total
            : (await store.ListOperationSearchesAsync(new OperationListQuery(null, null, 0, 0), ct)).Total;
        return new OperationsPageModel(BuildRows(result.Searches, ops), result.Total, page, pageSize,
            states is null ? null : state!.ToLowerInvariant(), kinds is null ? null : type!.ToLowerInvariant(),
			search, order.ToString().ToLowerInvariant(), descending, allTotal);
    }

    private IReadOnlyList<OperationRow> BuildRows(IReadOnlyList<SearchKey> keys, IReadOnlyList<BackgroundOperation> ops)
    {
        var jobs = (preload.GetStatus().Jobs ?? [])
            .ToDictionary(j => new SearchKey(j.Prefix, j.Filter));
        var byKey = ops.ToLookup(o => new SearchKey(o.Prefix, o.Filter));
        var rows = new List<OperationRow>(keys.Count);
        foreach (var key in keys)
        {
            var group = byKey[key].OrderByDescending(o => o.UpdatedAt).ToList();
            var capture = group.FirstOrDefault(o => o.Kind is OperationKind.CaptureLoad or OperationKind.WaybackRefresh);
            var pre = group.FirstOrDefault(o => o.Kind == OperationKind.PagePreload);
            jobs.TryGetValue(key, out var job);
            var saved = pre?.Completed ?? 0;
            var failed = pre?.Failed ?? 0;
            var pending = pre is null ? 0 : Math.Max(0, pre.Total - saved - failed);
            rows.Add(new OperationRow(key.Prefix, key.Filter, RowState(capture, pre), capture, pre, saved, job?.Skipped, failed,
                pending, job?.PerSecond ?? 0, job?.EtaSeconds, pre?.Error ?? capture?.Error));
        }
        return rows;
    }

    private static string RowState(BackgroundOperation? capture, BackgroundOperation? pre)
    {
        var states = new[] { capture?.State, pre?.State }.Where(s => s is not null).Select(s => OperationStates.Normalize(s!.Value)).ToList();
        if (states.Contains(OperationState.Running)) return "Running";
        if (states.Contains(OperationState.Queued) || states.Contains(OperationState.Interrupted)) return "Queued";
        if (states.Contains(OperationState.Paused)) return "Paused";
        if (states.Contains(OperationState.Failed)) return "Failed";
        if (states.Contains(OperationState.CompletedWithFailures)) return "CompletedWithFailures";
        return states.Count == 0 ? "Idle" : "Completed";
    }
}
