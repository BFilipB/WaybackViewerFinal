using Microsoft.Extensions.Logging;
using WaybackViewer.Models;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class ExtractionLogTests
{
    private sealed class ListLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Logs_found_counts_without_a_no_popularity_warning()
    {
        var log = new ListLogger();
        ExtractionLog.Page(log, "2020|x.com/a", "download", new PageMetadata(Views: 1200, Likes: 5, Source: "json-ld", Confidence: 0.8), 5000, 900, 3);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Debug && l.Text.Contains("views=1200") && l.Text.Contains("source=json-ld"));
        Assert.DoesNotContain(log.Lines, l => l.Text.Contains("parse.no_popularity"));
    }

    [Fact]
    public void Reports_pages_with_no_popularity_and_slow_parses()
    {
        var log = new ListLogger();
        ExtractionLog.Page(log, "k", "reprocess", new PageMetadata(), 100, 10, 900);
        Assert.Contains(log.Lines, l => l.Text.StartsWith("parse.no_popularity origin=reprocess"));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.StartsWith("parse.slow"));
    }
}
