using System.Text;
using Microsoft.AspNetCore.Mvc;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Controllers;

public class HomeController(CaptureCatalog catalog, PageService pages, PreloadCoordinator preload, IArchiveStore store) : Controller
{
    public async Task<IActionResult> Index(string? url, string? filter, bool refresh = false,
        string? pagination = null, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        var activeFilter = UrlInput.NormalizeFilter(filter);
        var mode = NormalizePaginationMode(pagination);
        page = Math.Max(1, page);
        pageSize = NormalizePageSize(pageSize);

        if (string.IsNullOrWhiteSpace(url))
            return View(new CaptureListViewModel("", activeFilter, [], null, PaginationMode: mode, PageSize: pageSize));

        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null)
            return View(new CaptureListViewModel(url.Trim(), activeFilter, [],
                "Enter a valid URL without spaces, for example example.com/blog/.", PaginationMode: mode, PageSize: pageSize));

        try
        {
            var result = await catalog.GetAsync(prefix, activeFilter, refresh, ct);
            if (result.Snapshot.Captures.Any(c => c.Capture.IsHtml && !c.Ready))
                preload.Enqueue(result.Snapshot.Captures, prefix, activeFilter);
            var total = result.Snapshot.Captures.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            IReadOnlyList<StoredCapture> shown = result.Snapshot.Captures;
            if (mode == "server")
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
                result.FromDatabase, result.Snapshot.CachedAt, mode, page, pageSize, total));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return View(new CaptureListViewModel(prefix, activeFilter, [], ex.Message, PaginationMode: mode, PageSize: pageSize));
        }
    }

    /// Visible text of one capture (database first, downloaded on demand otherwise).
    public async Task<IActionResult> Text(string ts, string url, CancellationToken ct)
    {
        if (!UrlInput.IsValidTimestamp(ts) || !UrlInput.IsValidCaptureUrl(url)) return BadRequest();

        var page = await pages.GetAsync(ts, url, includeHtml: false, ct);
        return Json(page is null
            ? new PageText(null, null, "", "The capture could not be loaded from the Wayback Machine.")
            : new PageText(page.Parsed.Title, page.Parsed.Description, page.Parsed.Text));
    }

    public IActionResult PreloadStatus() => Json(preload.GetStatus());

    public async Task<IActionResult> OperationStatus(CancellationToken ct = default) =>
        Json(await store.GetOperationsAsync(activeOnly: false, ct));

    [HttpPost]
    public async Task<IActionResult> StartSearch([FromForm] string? url, [FromForm] string? filter,
        [FromForm] bool refresh = false, [FromForm] string? pagination = null, [FromForm] int pageSize = 50)
    {
        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null) return BadRequest("Enter a valid URL without spaces.");
        var activeFilter = UrlInput.NormalizeFilter(filter);
        var mode = NormalizePaginationMode(pagination);
        pageSize = NormalizePageSize(pageSize);
        var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
        if (!refresh && await catalog.HasFreshSnapshotAsync(prefix, activeFilter))
            return Json(new { operationId = (long?)null, cached = true, prefix, filter = activeFilter, refresh, pagination = mode, pageSize });
        var operation = await store.CreateOrGetOperationAsync(prefix, activeFilter, kind, DateTimeOffset.UtcNow);
        _ = RunSearchAsync(prefix, activeFilter, refresh);
        return Json(new { operationId = operation.Id, prefix, filter = activeFilter, refresh, pagination = mode, pageSize });
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
