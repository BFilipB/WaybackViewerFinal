using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class ArchiveClientLoggingTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task Logs_http_errors_and_successful_fetches_with_timings()
    {
        var handler = new FakeHandler();
        handler.Enqueue(HttpStatusCode.TooManyRequests);
        handler.Enqueue(HttpStatusCode.OK, "body");
        var log = new ListLogger<ArchiveClient>();
        var options = Options.Create(new ArchiveOptions { MaxAttempts = 3, MinIntervalMs = 100, MaxIntervalMs = 10_000, MaxConcurrency = 2 });
        var client = new ArchiveClient(new HttpClient(handler), options, new FakeTimeSource(), log);

        Assert.Equal("body", await client.GetStringAsync("https://x/cdx/search"));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("archive.http_error kind=cdx host=cdx status=429 attempt=1/3"));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Debug && l.Text.Contains("archive.fetch kind=cdx host=cdx status=200 attempt=2"));
    }
}
