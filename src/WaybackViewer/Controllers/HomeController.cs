using System.Text;
using Microsoft.AspNetCore.Mvc;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Controllers;

public class HomeController(CaptureCatalog catalog, PageService pages, PreloadCoordinator preload, IArchiveStore store) : Controller
{
    public async Task<IActionResult> Index(string? url, string? filter, bool refresh = false, CancellationToken ct = default)
    {
        var activeFilter = UrlInput.NormalizeFilter(filter);

        if (string.IsNullOrWhiteSpace(url))
            return View(new CaptureListViewModel("", activeFilter, [], null));

        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null)
            return View(new CaptureListViewModel(url.Trim(), activeFilter, [],
                "Enter a valid URL without spaces, for example example.com/blog/."));

        try
        {
            var result = await catalog.GetAsync(prefix, activeFilter, refresh, ct);
            preload.Enqueue(result.Snapshot.Captures, prefix, activeFilter);
            return View(new CaptureListViewModel(prefix, activeFilter, result.Snapshot.Captures, null,
                result.FromDatabase, result.Snapshot.CachedAt));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return View(new CaptureListViewModel(prefix, activeFilter, [], ex.Message));
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
    public async Task<IActionResult> StartSearch([FromForm] string? url, [FromForm] string? filter, [FromForm] bool refresh = false)
    {
        var prefix = UrlInput.NormalizePrefix(url);
        if (prefix is null) return BadRequest("Enter a valid URL without spaces.");
        var activeFilter = UrlInput.NormalizeFilter(filter);
        var kind = refresh ? OperationKind.WaybackRefresh : OperationKind.CaptureLoad;
        var operation = await store.CreateOrGetOperationAsync(prefix, activeFilter, kind, DateTimeOffset.UtcNow);
        _ = RunSearchAsync(prefix, activeFilter, refresh);
        return Json(new { operationId = operation.Id, prefix, filter = activeFilter, refresh });
    }

    [HttpPost]
    public IActionResult Pause([FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        preload.Pause(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    private async Task RunSearchAsync(string prefix, string filter, bool refresh)
    {
        try
        {
            var result = await catalog.GetAsync(prefix, filter, refresh).ConfigureAwait(false);
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
    public IActionResult Resume([FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        preload.Resume(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    [HttpPost]
    public IActionResult Stop([FromQuery] string? url = null, [FromQuery] string? filter = null)
    {
        preload.Stop(NormalizeOptionalUrl(url), UrlInput.NormalizeFilter(filter));
        return NoContent();
    }

    private static string? NormalizeOptionalUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : UrlInput.NormalizePrefix(url);

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
