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

public sealed class PageLoader(IArchiveClient archive, IOptions<PreloadOptions> options) : IPageLoader
{
    public async Task<NewPage> LoadAsync(Capture capture, CancellationToken ct = default)
    {
        var html = await archive.GetStringAsync(ArchiveUrls.Raw(capture.Timestamp, capture.Original), ct).ConfigureAwait(false);

        if (FrameHtml.LooksBinary(html))
            throw new InvalidDataException("The capture is not an HTML page.");

        var max = options.Value.MaxHtmlChars;
        if (html.Length > max) html = html[..max];

        var parsed = PageParser.Parse(html);
        return new NewPage(capture, HtmlCodec.Compress(html), parsed, DateTimeOffset.UtcNow);
    }
}
