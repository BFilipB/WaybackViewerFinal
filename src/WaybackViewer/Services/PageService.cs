using System.Collections.Concurrent;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// One page on demand: the database if we have it, otherwise download -> save -> return.
/// Concurrent requests for the same missing page share a single download and a single database write.
public sealed class PageService(IArchiveStore store, IPageLoader loader, PreloadCoordinator preload)
{
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

    private async Task<NewPage?> LoadAndSaveAsync(string key, string timestamp, string original)
    {
        try
        {
            NewPage page;
            try
            {
                page = await loader.LoadAsync(new Capture(timestamp, original, "text/html", "200")).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
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
