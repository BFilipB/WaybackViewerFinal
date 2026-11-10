using Microsoft.Extensions.Logging;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class ObservabilityTests
{
    private sealed class ListLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (sink) sink.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    [Theory]
    [InlineData("20200101000000|https://WWW.Example.com/a", "www.example.com")]
    [InlineData("20200101000000|not a url", "unknown")]
    public void Host_of_capture_key_is_lowercase_host_or_unknown(string key, string expected) =>
        Assert.Equal(expected, ExtractionStats.HostOfKey(key));

    [Fact]
    public void Extraction_stats_aggregate_per_host_and_reset_on_flush()
    {
        var logs = new List<string>();
        ExtractionStats.Flush(new ListLogger([]));   // isolate from other tests' recorded pages
        var host = "stats-" + Guid.NewGuid().ToString("N")[..6] + ".test";
        ExtractionStats.Record($"1|https://{host}/a", new PageMetadata { Views = 10, Source = "jsonld" }, 2);
        ExtractionStats.Record($"2|https://{host}/b", new PageMetadata { Source = "meta" }, 4);
        ExtractionStats.Record($"3|https://{host}/c", null, 600);

        ExtractionStats.Flush(new ListLogger(logs));

        var line = Assert.Single(logs, l => l.Contains("host=" + host));
        Assert.Contains("pages=3", line);
        Assert.Contains("views=1", line);
        Assert.Contains("noPopularity=2", line);
        Assert.Contains("slow=1", line);
        Assert.Contains("jsonld:1", line);
        Assert.Contains("none:1", line);

        logs.Clear();
        ExtractionStats.Flush(new ListLogger(logs));
        Assert.DoesNotContain(logs, l => l.Contains("host=" + host));
    }

    [Fact]
    public void Extraction_stats_split_measured_from_estimated_and_reset_clears()
    {
        var logs = new List<string>();
        ExtractionStats.Reset();
        var host = "est-" + Guid.NewGuid().ToString("N")[..6] + ".test";
        ExtractionStats.Record($"1|https://{host}/a", new PageMetadata { Views = 0, EngagementScore = 0 }, 1);   // explicit zero is measured
        ExtractionStats.Record($"2|https://{host}/b", new PageMetadata { EngagementScore = 40 }, 1);             // estimate only
        ExtractionStats.Record($"3|https://{host}/c", new PageMetadata(), 1);                                     // unknown

        ExtractionStats.Flush(new ListLogger(logs));
        var line = Assert.Single(logs, l => l.Contains("host=" + host));
        Assert.Contains("measured=1", line);
        Assert.Contains("estimated=1", line);

        ExtractionStats.Record($"4|https://{host}/d", null, 1);
        ExtractionStats.Reset();
        logs.Clear();
        ExtractionStats.Flush(new ListLogger(logs));
        Assert.DoesNotContain(logs, l => l.Contains("host=" + host));
    }

    [Fact]
    public void Html_store_reset_zeroes_counters()
    {
        HtmlFileStore.Saves = 5; HtmlFileStore.LoadMisses = 3;
        HtmlFileStore.ResetCounters();
        Assert.Equal(0, Interlocked.Read(ref HtmlFileStore.Saves));
        Assert.Equal(0, Interlocked.Read(ref HtmlFileStore.LoadMisses));
    }

    [Fact]
    public void File_log_lines_carry_the_run_id()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-runid-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new FileLoggerProvider(dir))
                provider.CreateLogger("Test").LogInformation("hello.event x=1");
            var text = File.ReadAllText(Directory.GetFiles(dir, "wayback-*.log").Single());
            Assert.Contains($"[{RunInfo.Id}] Test hello.event x=1", text);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Html_store_counts_saves_and_load_misses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-html-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HtmlFileStore(dir);
            var capture = new Capture("20200101000000", "https://a.test/x", "text/html", "200");
            var saves = Interlocked.Read(ref HtmlFileStore.Saves);
            var misses = Interlocked.Read(ref HtmlFileStore.LoadMisses);

            Assert.Null(await store.LoadAsync(new Capture("20200101000000", "https://a.test/missing", "text/html", "200")));
            await store.SaveAsync(capture, [1, 2, 3]);

            Assert.True(Interlocked.Read(ref HtmlFileStore.Saves) >= saves + 1);
            Assert.True(Interlocked.Read(ref HtmlFileStore.LoadMisses) >= misses + 1);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
