using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class ParsingDiagnosticsTests
{
    private sealed class ListLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public void Cdx_parser_reports_skipped_malformed_rows()
    {
        var json = "[[\"timestamp\",\"original\",\"mimetype\",\"statuscode\"],[\"1\"],null,[\"20200101000000\",\"u\",\"m\",\"200\"]]";
        var list = CdxParser.Parse(json, out var skipped);
        Assert.Single(list);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void Cdx_parser_reports_zero_skipped_for_empty_input()
    {
        Assert.Empty(CdxParser.Parse("", out var skipped));
        Assert.Equal(0, skipped);
    }

    [Fact]
    public async Task Page_loader_logs_truncation_of_oversized_html()
    {
        var logs = new List<string>();
        var capture = new Capture("20200101000000", "https://a.com/", "text/html", "200");
        var archive = new FakeArchiveClient();
        archive.Route(ArchiveUrls.Raw(capture.Timestamp, capture.Original), "<html><body>" + new string('x', 500) + "</body></html>");
        var loader = new PageLoader(archive, Options.Create(new PreloadOptions { MaxHtmlChars = 100 }), new ListLogger<PageLoader>(logs));

        await loader.LoadAsync(capture, CancellationToken.None);

        Assert.Contains(logs, l => l.StartsWith("Warning") && l.Contains("parse.truncated") && l.Contains("maxChars=100"));
    }

    [Fact]
    public async Task Page_loader_logs_rejection_of_binary_content()
    {
        var logs = new List<string>();
        var capture = new Capture("20200101000000", "https://a.com/x.png", "image/png", "200");
        var archive = new FakeArchiveClient();
        archive.Route(ArchiveUrls.Raw(capture.Timestamp, capture.Original), "\u0000\u0001\u0002PNG\u0000\u0000\u0000");
        var loader = new PageLoader(archive, Options.Create(new PreloadOptions()), new ListLogger<PageLoader>(logs));

        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(capture, CancellationToken.None));

        Assert.Contains(logs, l => l.Contains("parse.rejected") && l.Contains("reason=binary"));
    }
}
