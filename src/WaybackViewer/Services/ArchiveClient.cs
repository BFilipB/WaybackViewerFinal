using System.Net;
using Microsoft.Extensions.Options;

namespace WaybackViewer.Services;

public interface IArchiveClient
{
    Task<string> GetStringAsync(string url, CancellationToken ct = default);
}

/// The archive answered with a non-success status. RetryAfter carries the archive's Retry-After hint, if any.
public sealed class ArchiveRequestException(HttpStatusCode statusCode, TimeSpan? retryAfter)
    : HttpRequestException($"Archive request failed with HTTP {(int)statusCode}.", null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// Polite, fast client for web.archive.org: bounded concurrency, adaptive request spacing that backs off on
/// HTTP 429 / network failures and recovers on success, retry with Retry-After / exponential backoff, and
/// de-duplication of identical in-flight requests.
public sealed class ArchiveClient : IArchiveClient
{
    private sealed class SharedRequest
    {
        public readonly CancellationTokenSource Cts = new();
        public Task<string> Task = null!;
        public int Waiters;
    }

    private readonly HttpClient http;
    private readonly ArchiveOptions options;
    private readonly ITimeSource time;
    private readonly SemaphoreSlim gate;
    private readonly object paceLock = new();
    private readonly TimeSpan minInterval;
    private readonly TimeSpan maxInterval;
    private TimeSpan interval;
    private DateTime nextRequestUtc = DateTime.MinValue;
    private readonly object inflightLock = new();
    private readonly Dictionary<string, SharedRequest> inflight = new(StringComparer.Ordinal);

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

    /// Identical in-flight requests share one download. A caller giving up does not cancel the shared download
    /// while others still wait for it; once every caller has given up, the download itself is cancelled.
    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        SharedRequest shared;
        lock (inflightLock)
        {
            if (!inflight.TryGetValue(url, out shared!))
            {
                shared = new SharedRequest();
                inflight[url] = shared;
                shared.Task = RunAsync(url, shared);
            }
            shared.Waiters++;
        }

        try
        {
            return await shared.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (inflightLock)
            {
                if (--shared.Waiters == 0 && !shared.Task.IsCompleted)
                {
                    shared.Cts.Cancel();
                    if (inflight.TryGetValue(url, out var current) && ReferenceEquals(current, shared)) inflight.Remove(url);
                }
            }
        }
    }

    private async Task<string> RunAsync(string url, SharedRequest shared)
    {
        try
        {
            await Task.Yield();   // never run the download inside the caller's lock
            return await FetchAsync(url, shared.Cts.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (inflightLock)
            {
                if (inflight.TryGetValue(url, out var current) && ReferenceEquals(current, shared)) inflight.Remove(url);
            }
        }
    }

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await WaitForSlotAsync(ct).ConfigureAwait(false);

                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsNetworkFailure(ex, ct) && attempt < options.MaxAttempts)
                {
                    OnRateLimited();   // connection refused / reset / timeout: the archive is usually throttling us
                    await time.DelayAsync(Backoff(attempt, null), ct).ConfigureAwait(false);
                    continue;
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        OnSuccess();
                        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    }

                    var code = (int)response.StatusCode;
                    var retryable = code is 408 or 429 || code >= 500;
                    if (code == 429) OnRateLimited();
                    if (!retryable || attempt >= options.MaxAttempts)
                        throw new ArchiveRequestException(response.StatusCode, RetryAfter(response));

                    await time.DelayAsync(Backoff(attempt, response), ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);   // HttpClient timeout

    private async Task WaitForSlotAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (paceLock)
        {
            var now = time.UtcNow;
            var start = nextRequestUtc > now ? nextRequestUtc : now;
            wait = start - now;
            nextRequestUtc = start + interval;
        }
        if (wait > TimeSpan.Zero) await time.DelayAsync(wait, ct).ConfigureAwait(false);
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

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        return retryAfter is { } ra && ra > TimeSpan.Zero ? ra : null;
    }

    private static TimeSpan Backoff(int attempt, HttpResponseMessage? response)
    {
        if (response is not null && RetryAfter(response) is { } ra)
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
