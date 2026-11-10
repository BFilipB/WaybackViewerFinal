using System.Diagnostics;
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

    private readonly ConcurrentDictionary<string, byte> backfills = new(StringComparer.Ordinal);
    private const int BackfillBatch = 100;

    /// Starts (once per search) a background pass that re-reads saved pages without a publish date from their stored HTML.
    /// It is derived from the database state, so it simply continues after a restart the next time it is requested.
    public void StartPublishedBackfill(string prefix, string filter)
    {
        var key = prefix + "|" + filter;
        if (!backfills.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await BackfillPublishedAsync(prefix, filter, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Published-date backfill for {Prefix} failed", prefix); }
            finally { backfills.TryRemove(key, out _); }
        });
    }

    /// Returns how many pages got a publish date. Only the metadata rows are rewritten.
    public async Task<int> BackfillPublishedAsync(string prefix, string filter, CancellationToken ct = default)
    {
        var updated = 0;
        var seen = 0;
        var batches = 0;
        var runStart = Stopwatch.GetTimestamp();
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await store.GetPagesMissingPublishedAsync(prefix, filter, after, BackfillBatch, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                log.LogInformation("backfill.done prefix={Prefix} filter={Filter} batches={Batches} seen={Seen} updated={Updated} ms={Ms:F0}", prefix, filter, batches, seen, updated, Stopwatch.GetElapsedTime(runStart).TotalMilliseconds);
                return updated;
            }
            batches++; seen += batch.Count;
            after = batch.Max(b => b.Id);
            var fresh = await ReparseBatchAsync(batch, m => m.PublishedAt is not null, ct).ConfigureAwait(false);
            await store.UpdateMetadataAsync(fresh, ct).ConfigureAwait(false);
            updated += fresh.Count;
        }
    }

	private readonly ConcurrentDictionary<string, byte> reprocesses = new(StringComparer.Ordinal);

	/// Starts (once per search) a background pass that re-parses pages stored by an older extractor.
	/// Derived from database state, so it resumes after a restart.
	public void StartStaleReprocess(string prefix, string filter)
	{
		var key = prefix + "|" + filter;
		if (!reprocesses.TryAdd(key, 0)) return;
		_ = Task.Run(async () =>
		{
			try { await ReprocessStaleAsync(prefix, filter, CancellationToken.None).ConfigureAwait(false); }
			catch (Exception ex) { log.LogWarning(ex, "Extractor reprocess for {Prefix} failed", prefix); }
			finally { reprocesses.TryRemove(key, out _); }
		});
	}

    private static readonly int ParseDegree = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

    /// Reads each saved page of the batch one at a time (no disk-head thrashing), parses on a bounded number of cores
    /// and returns the new metadata. Pages without saved HTML are skipped and logged.
    private async Task<List<(Capture Capture, PageMetadata Metadata)>> ReparseBatchAsync(
        IReadOnlyList<StoredCapture> batch, Func<PageMetadata, bool> keep, CancellationToken ct)
    {
        var loaded = new List<(Capture Capture, byte[] Gz)>(batch.Count);
        foreach (var item in batch)
        {
            ct.ThrowIfCancellationRequested();
            var stored = await store.GetPageAsync(item.Capture.Timestamp, item.Capture.Original, includeHtml: true, ct).ConfigureAwait(false);
            if (stored?.HtmlGz is null) { log.LogInformation("reprocess.skipped key={Key} reason=no_saved_html", item.Capture.Key); continue; }
            loaded.Add((stored.Capture, stored.HtmlGz));
        }
        var results = new (Capture Capture, PageMetadata? Metadata)[loaded.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, loaded.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParseDegree, CancellationToken = ct },
            (i, token) =>
            {
                var pStart = Stopwatch.GetTimestamp();
                var html = HtmlCodec.Decompress(loaded[i].Gz);
                var parsed = PageParser.Parse(html);
                var meta = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
                results[i] = (loaded[i].Capture, meta);
                ExtractionLog.Page(log, loaded[i].Capture.Key, "reprocess", meta, html.Length, parsed.Text.Length, Stopwatch.GetElapsedTime(pStart).TotalMilliseconds);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return results.Where(r => r.Metadata is { } m && keep(m)).Select(r => (r.Capture, r.Metadata!)).ToList();
    }

    /// Returns how many pages were refreshed. Only the metadata rows are rewritten (one transaction per batch).
    public async Task<int> ReprocessStaleAsync(string prefix, string filter, CancellationToken ct = default)
    {
        var updated = 0;
        var seen = 0;
        var runStart = Stopwatch.GetTimestamp();
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await store.GetPagesWithStaleExtractorAsync(prefix, filter, ExtractorVersion.Current, after, BackfillBatch, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                if (seen > 0)
                    log.LogInformation("reprocess.done prefix={Prefix} filter={Filter} version={Version} seen={Seen} updated={Updated} skippedNoHtml={Skipped} ms={Ms:F0}", prefix, filter, ExtractorVersion.Current, seen, updated, seen - updated, Stopwatch.GetElapsedTime(runStart).TotalMilliseconds);
                return updated;
            }
            seen += batch.Count;
            after = batch.Max(b => b.Id);
            var started = Stopwatch.GetTimestamp();
            var fresh = await ReparseBatchAsync(batch, _ => true, ct).ConfigureAwait(false);
            var parsedAt = Stopwatch.GetTimestamp();
            await store.UpdateMetadataAsync(fresh, ct).ConfigureAwait(false);
            updated += fresh.Count;
            log.LogInformation("reprocess.batch prefix={Prefix} pages={Count} of={Of} readParseMs={ParseMs:F0} writeMs={WriteMs:F0}", prefix, fresh.Count, batch.Count,
                Stopwatch.GetElapsedTime(started, parsedAt).TotalMilliseconds, Stopwatch.GetElapsedTime(parsedAt).TotalMilliseconds);
        }
    }

	private static bool HasEstimate(PageMetadata m) =>
        m.Views.HasValue || m.Likes.HasValue || m.Comments.HasValue || m.Shares.HasValue || m.Score.HasValue || m.EngagementScore is > 0;

    public async Task<PageMetadata?> GetPopularityMetadataAsync(string timestamp, string original, CancellationToken ct = default)
    {
        var stored = await store.GetPageAsync(timestamp, original, includeHtml: true, ct).ConfigureAwait(false);
        if (stored?.Parsed.Metadata is { } known && known.Confidence >= 0.2 && HasEstimate(known) && known.Comments.HasValue) return known;

        if (stored?.HtmlGz is not null)
        {
            // Saved without popularity data, or without a comment count (older extractor): rework it from the saved HTML (no download needed).
            var pStart = Stopwatch.GetTimestamp();
            var html = HtmlCodec.Decompress(stored.HtmlGz);
            var parsed = PageParser.Parse(html);
            parsed = parsed with { Metadata = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text) };
            ExtractionLog.Page(log, stored.Capture.Key, "on-demand", parsed.Metadata, html.Length, parsed.Text.Length, Stopwatch.GetElapsedTime(pStart).TotalMilliseconds);
            if (stored.Parsed.Metadata is { } old && parsed.Metadata is { } fresh && fresh.Comments is null && fresh.Confidence <= old.Confidence)
                return old;
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
