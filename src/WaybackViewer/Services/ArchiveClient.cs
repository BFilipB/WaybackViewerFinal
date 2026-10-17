using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;

namespace WaybackViewer.Services;

public interface IArchiveClient
{
    Task<string> GetStringAsync(string url, CancellationToken ct = default);
}

/// Polite, fast client for web.archive.org: bounded concurrency, adaptive request spacing that backs off on
/// HTTP 429 and recovers on success, retry with Retry-After / exponential backoff, and de-duplication of
/// identical in-flight requests.
public sealed class ArchiveClient : IArchiveClient
{
    private readonly HttpClient http;
    private readonly ArchiveOptions options;
    private readonly ITimeSource time;
    private readonly SemaphoreSlim gate;
    private readonly object paceLock = new();
    private readonly TimeSpan minInterval;
    private readonly TimeSpan maxInterval;
    private TimeSpan interval;
    private DateTime nextRequestUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> inflight = new(StringComparer.Ordinal);

    public ArchiveClient(HttpClient http, IOptions<ArchiveOptions> options, ITimeSource time)
    {
        this.http = http;
        this.options = options.Value;
        this.time = time;
        gate = new SemaphoreSlim(Math.Max(1, this.options.MaxConcurrency));
        minInterval = TimeSpan.FromMilliseconds(Math.Max(0, this.options.MinIntervalMs));
        maxInterval = TimeSpan.FromMilliseconds(Math.Max(this.options.MinIntervalMs, this.options.MaxIntervalMs));
        interval = minInterval;
    }

    public TimeSpan CurrentInterval { get { lock (paceLock) return interval; } }

    public Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        var lazy = inflight.GetOrAdd(url,
            u => new Lazy<Task<string>>(() => RunAsync(u), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(ct); // a caller giving up must not cancel the shared download
    }

    private async Task<string> RunAsync(string url)
    {
        try { return await FetchAsync(url).ConfigureAwait(false); }
        finally { inflight.TryRemove(url, out _); }
    }

    private async Task<string> FetchAsync(string url)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await WaitForSlotAsync().ConfigureAwait(false);

                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < options.MaxAttempts)
                {
                    await time.DelayAsync(Backoff(attempt, null)).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        OnSuccess();
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }

                    var code = (int)response.StatusCode;
                    var retryable = code is 408 or 429 || code >= 500;
                    if (!retryable || attempt >= options.MaxAttempts)
                        throw new HttpRequestException($"Archive request failed with HTTP {code}.", null, response.StatusCode);

                    if (code == 429) OnRateLimited();
                    await time.DelayAsync(Backoff(attempt, response)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WaitForSlotAsync()
    {
        TimeSpan wait;
        lock (paceLock)
        {
            var now = time.UtcNow;
            var start = nextRequestUtc > now ? nextRequestUtc : now;
            wait = start - now;
            nextRequestUtc = start + interval;
        }
        if (wait > TimeSpan.Zero) await time.DelayAsync(wait).ConfigureAwait(false);
    }

    private void OnSuccess()
    {
        lock (paceLock)
        {
            var faster = TimeSpan.FromTicks((long)(interval.Ticks * 0.9));
            interval = faster > minInterval ? faster : minInterval;
        }
    }

    private void OnRateLimited()
    {
        lock (paceLock)
        {
            var slower = TimeSpan.FromTicks(Math.Max(interval.Ticks, TimeSpan.FromSeconds(1).Ticks) * 2);
            interval = slower < maxInterval ? slower : maxInterval;
            var pauseUntil = time.UtcNow + interval;
            if (nextRequestUtc < pauseUntil) nextRequestUtc = pauseUntil;
        }
    }

    private static TimeSpan Backoff(int attempt, HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response?.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return ra < TimeSpan.FromMinutes(1) ? ra : TimeSpan.FromMinutes(1);

        var seconds = Math.Min(30, Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 750));
    }

    public static HttpClient CreateHttpClient(ArchiveOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(8, options.MaxConcurrency * 2),
            EnableMultipleHttp2Connections = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WaybackViewer/2.0");
        return client;
    }
}
