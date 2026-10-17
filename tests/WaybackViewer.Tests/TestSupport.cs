using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using WaybackViewer;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Clock whose delays complete instantly but advance the clock, so backoff/pacing is testable without waiting.
public sealed class FakeTimeSource : ITimeSource
{
    private readonly object gate = new();
    private DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public List<TimeSpan> Delays { get; } = new();

    public DateTime UtcNow { get { lock (gate) return now; } }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
    {
        lock (gate) { Delays.Add(delay); now += delay; }
        return Task.CompletedTask;
    }

    public void Advance(TimeSpan span) { lock (gate) now += span; }
    public double TotalDelayMs { get { lock (gate) return Delays.Sum(d => d.TotalMilliseconds); } }
}

/// HttpMessageHandler that replays queued responses (or throws) and counts calls.
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> queue = new();
    public int Calls;
    public TaskCompletionSource? Hold { get; set; }

    public void Enqueue(HttpStatusCode code, string body = "ok", Action<HttpResponseMessage>? configure = null) =>
        queue.Enqueue(() =>
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent(body) };
            configure?.Invoke(response);
            return response;
        });

    public void EnqueueThrow(Exception ex) => queue.Enqueue(() => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (Hold is not null) await Hold.Task;
        return queue.TryDequeue(out var next) ? next() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
    }
}

/// In-process stand-in for web.archive.org.
public sealed class FakeArchiveClient : IArchiveClient
{
    private readonly ConcurrentDictionary<string, Func<string>> routes = new();
    private readonly ConcurrentDictionary<string, int> counts = new();
    private int total;

    public Func<string, Task>? BeforeResponse { get; set; }
    public int TotalCalls => Volatile.Read(ref total);
    public int Calls(string url) => counts.TryGetValue(url, out var c) ? c : 0;

    public void Route(string url, string body) => routes[url] = () => body;
    public void Route(string url, Func<string> body) => routes[url] = body;
    public void Remove(string url) => routes.TryRemove(url, out _);

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        counts.AddOrUpdate(url, 1, (_, c) => c + 1);
        Interlocked.Increment(ref total);
        if (BeforeResponse is not null) await BeforeResponse(url);
        if (!routes.TryGetValue(url, out var body))
            throw new HttpRequestException($"No route for {url}", null, HttpStatusCode.NotFound);
        return body();
    }
}

public sealed class FakePageLoader : IPageLoader
{
    public ConcurrentQueue<string> Started { get; } = new();
    public ConcurrentDictionary<string, byte> FailKeys { get; } = new();
    public Func<Capture, Task>? Hook { get; set; }

    public async Task<NewPage> LoadAsync(Capture capture, CancellationToken ct = default)
    {
        Started.Enqueue(capture.Key);
        if (Hook is not null) await Hook(capture);
        if (FailKeys.ContainsKey(capture.Key)) throw new HttpRequestException("boom");
        var html = SampleData.Html(capture.Original, "Hello " + capture.Timestamp);
        return new NewPage(capture, HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow);
    }
}

/// Store whose page saves always fail (for failure-path tests).
public sealed class ThrowingSaveStore : IArchiveStore
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<SearchSnapshot?> GetSearchAsync(string prefix, string filter, CancellationToken ct = default) => Task.FromResult<SearchSnapshot?>(null);
    public Task<SearchSnapshot> SaveSearchAsync(string prefix, string filter, IReadOnlyList<Capture> captures, DateTimeOffset cachedAt, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<StoredPage?> GetPageAsync(string timestamp, string original, bool includeHtml, CancellationToken ct = default) => Task.FromResult<StoredPage?>(null);
    public Task SavePagesAsync(IReadOnlyList<NewPage> pages, CancellationToken ct = default) => throw new InvalidOperationException("disk full");
    public Task<BackgroundOperation?> FindActiveOperationAsync(string prefix, string filter, OperationKind kind, CancellationToken ct = default) => Task.FromResult<BackgroundOperation?>(null);
    public Task<BackgroundOperation> CreateOrGetOperationAsync(string prefix, string filter, OperationKind kind, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BackgroundOperation?> GetOperationAsync(long id, CancellationToken ct = default) => Task.FromResult<BackgroundOperation?>(null);
    public Task<IReadOnlyList<BackgroundOperation>> GetOperationsAsync(bool activeOnly = false, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackgroundOperation>>([]);
    public Task UpdateOperationAsync(BackgroundOperation operation, CancellationToken ct = default) => Task.CompletedTask;
}

public static class SampleData
{
    public static Capture Capture(int i, string mime = "text/html", string status = "200") =>
        new(new DateTime(2020, 1, 1).AddMinutes(i).ToString("yyyyMMddHHmmss"), $"https://example.com/page{i}", mime, status);

    public static List<Capture> Captures(int count) => Enumerable.Range(0, count).Select(i => Capture(i)).ToList();

    public static IReadOnlyList<StoredCapture> Stored(IEnumerable<Capture> captures, bool ready = false) =>
        captures.Select((c, i) => new StoredCapture(i + 1, c, ready)).ToList();

    public static string Html(string title, string body) =>
        $"<html><head><title>{title}</title><meta name=\"description\" content=\"desc of {title}\"></head><body><p>{body}</p></body></html>";

    public static string CdxJson(IEnumerable<Capture> captures)
    {
        var rows = new List<string[]> { new[] { "timestamp", "original", "mimetype", "statuscode" } };
        rows.AddRange(captures.Select(c => new[] { c.Timestamp, c.Original, c.MimeType, c.StatusCode }));
        return JsonSerializer.Serialize(rows);
    }

    public static async Task Eventually(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Condition was not met in time.");
    }

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 10_000) =>
        await Eventually(() => Task.FromResult(condition()), timeoutMs);
}
