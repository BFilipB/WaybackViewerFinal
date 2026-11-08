using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// One page on demand: the database if we have it, otherwise download -> save -> return.
/// Concurrent requests for the same missing page share a single download and a single database write.
public sealed class PageService(IArchiveStore store, IPageLoader loader, PreloadCoordinator preload,
    ILogger<PageService>? logger = null)
{
    private readonly ILogger<PageService> log = logger ?? NullLogger<PageService>.Instance;
    private readonly ConcurrentDictionary<string, Lazy<Task<NewPage?>>> loads = new(StringComparer.Ordinal);

    public async Task<StoredPage?> GetAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default)
    {
        var stored = await store.GetPageAsync(timestamp, original, includeHtml, ct).ConfigureAwait(false);
        if (stored is not null) return stored;

        var key = timestamp + "|" + original;
        var load = loads.GetOrAdd(key, k => new Lazy<Task<NewPage?>>(
            () => LoadAndSaveAsync(k, timestamp, original), LazyThreadSafetyMode.ExecutionAndPublication));

        var page = await load.Value.WaitAsync(ct).ConfigureAwait(false); // a caller giving up does not cancel the shared load
        return page is null
            ? null
            : new StoredPage(page.Capture, includeHtml ? page.HtmlGz : null, page.Parsed, page.FetchedAt);
    }

    /// True when the capture is already saved (metadata only: the HTML payload is not read).
    public async Task<bool> IsStoredAsync(string timestamp, string original, CancellationToken ct = default) =>
        await store.GetPageAsync(timestamp, original, includeHtml: false, ct).ConfigureAwait(false) is not null;

    private static bool HasEstimate(PageMetadata m) =>
        m.Views.HasValue || m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.Score.HasValue || m.EngagementScore is > 0;

    public async Task<PageMetadata?> GetPopularityMetadataAsync(string timestamp, string original, CancellationToken ct = default)
    {
        var stored = await store.GetPageAsync(timestamp, original, includeHtml: true, ct).ConfigureAwait(false);
        if (stored?.Parsed.Metadata is { } known && (known.Confidence >= 0.2 && HasEstimate(known))) return known;

        if (stored?.HtmlGz is not null)
        {
            // Saved earlier without popularity data: work it out from the saved HTML (no download needed).
            var html = HtmlCodec.Decompress(stored.HtmlGz);
            var parsed = PageParser.Parse(html);
            parsed = parsed with { Metadata = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text) };
            await store.SavePagesAsync([new NewPage(stored.Capture, stored.HtmlGz, parsed, stored.FetchedAt)], ct).ConfigureAwait(false);
            return parsed.Metadata;
        }

        // Sorting/filtering must not trigger a fresh archive fetch for every missing capture.
        // Unknown metadata is treated as zero rather than retrying the file from the archive.
        return null;
    }


    private async Task<NewPage?> LoadAndSaveAsync(string key, string timestamp, string original)
    {
        try
        {
            NewPage page;
            try
            {
                page = await loader.LoadAsync(new Capture(timestamp, original, "text/html", "200")).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException ||
                ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                log.LogInformation("Capture {Key} is not available as HTML ({Reason})", key, ex.GetType().Name);
                return null;   // not found / not HTML: a normal outcome, the caller shows "could not be loaded"
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Loading capture {Key} on demand failed", key);
                return null;   // transient or permanent archive failure; already logged with details
            }

            await store.SavePagesAsync([page]).ConfigureAwait(false);
            preload.MarkDone(key); // no need to download it again in the background
            return page;
        }
        finally
        {
            loads.TryRemove(key, out _);
        }
    }
}
