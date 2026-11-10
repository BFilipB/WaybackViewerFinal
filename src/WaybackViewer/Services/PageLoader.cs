using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

public interface IPageLoader
{
    /// Downloads one capture, parses it once and compresses it. Throws when the download fails or is not HTML.
    Task<NewPage> LoadAsync(Capture capture, CancellationToken ct = default);
}

public sealed class PageLoader(IArchiveClient archive, IOptions<PreloadOptions> options,
    Microsoft.Extensions.Logging.ILogger<PageLoader>? logger = null) : IPageLoader
{
    public async Task<NewPage> LoadAsync(Capture capture, CancellationToken ct = default)
    {
        var html = await archive.GetStringAsync(ArchiveUrls.Raw(capture.Timestamp, capture.Original), ct).ConfigureAwait(false);

        if (FrameHtml.LooksBinary(html))
        {
            logger?.LogWarning("parse.rejected key={Key} reason=binary htmlChars={Html}", capture.Key, html.Length);
            throw new InvalidDataException("The capture is not an HTML page.");
        }

        var max = options.Value.MaxHtmlChars;
        if (html.Length > max)
        {
            logger?.LogWarning("parse.truncated key={Key} htmlChars={Html} maxChars={Max}", capture.Key, html.Length, max);
            html = html[..max];
        }

        var parseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var parsed = PageParser.Parse(html);
        parsed = parsed with { Metadata = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text) };
        if (logger is not null)
            ExtractionLog.Page(logger, capture.Key, "download", parsed.Metadata, html.Length, parsed.Text.Length,
                System.Diagnostics.Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds);
        return new NewPage(capture, HtmlCodec.Compress(html), parsed, DateTimeOffset.UtcNow);
    }
}
