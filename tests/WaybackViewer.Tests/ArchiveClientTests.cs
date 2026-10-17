using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class ArchiveClientTests
{
    private static (ArchiveClient Client, FakeHandler Handler, FakeTimeSource Time) Create(
        int maxAttempts = 3, int minIntervalMs = 300, int maxConcurrency = 4)
    {
        var handler = new FakeHandler();
        var time = new FakeTimeSource();
        var options = Options.Create(new ArchiveOptions
        {
            MaxAttempts = maxAttempts, MinIntervalMs = minIntervalMs, MaxIntervalMs = 10_000, MaxConcurrency = maxConcurrency
        });
        return (new ArchiveClient(new HttpClient(handler), options, time), handler, time);
    }

    [Fact]
    public async Task Returns_the_body_on_success()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, "hello");
        Assert.Equal("hello", await client.GetStringAsync("https://x/1"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retries_server_errors_with_backoff()
    {
        var (client, handler, time) = Create();
        handler.Enqueue(HttpStatusCode.InternalServerError);
        handler.Enqueue(HttpStatusCode.OK, "second time");

        Assert.Equal("second time", await client.GetStringAsync("https://x/1"));
        Assert.Equal(2, handler.Calls);
        Assert.Contains(time.Delays, d => d >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Retries_network_errors()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("connection reset"));
        handler.Enqueue(HttpStatusCode.OK, "recovered");
        Assert.Equal("recovered", await client.GetStringAsync("https://x/1"));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Honors_retry_after_and_slows_down_on_429()
    {
        var (client, handler, time) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));
        handler.Enqueue(HttpStatusCode.OK, "fine");

        Assert.Equal("fine", await client.GetStringAsync("https://x/1"));
        Assert.Contains(time.Delays, d => d == TimeSpan.FromSeconds(7));
        Assert.True(client.CurrentInterval > TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task Speeds_up_again_after_successes()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests);
        handler.Enqueue(HttpStatusCode.OK);
        await client.GetStringAsync("https://x/0");
        var slowed = client.CurrentInterval;

        for (var i = 1; i <= 40; i++) await client.GetStringAsync("https://x/" + i);

        Assert.True(client.CurrentInterval < slowed);
        Assert.Equal(TimeSpan.FromMilliseconds(300), client.CurrentInterval);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var (client, handler, _) = Create(maxAttempts: 3);
        for (var i = 0; i < 5; i++) handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://x/1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Does_not_retry_permanent_errors()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.NotFound);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://x/missing"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Identical_in_flight_requests_share_one_download()
    {
        var (client, handler, _) = Create();
        handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Enqueue(HttpStatusCode.OK, "shared");

        var first = client.GetStringAsync("https://x/same");
        var second = client.GetStringAsync("https://x/same");
        var third = client.GetStringAsync("https://x/same");
        handler.Hold.SetResult();

        Assert.Equal(new[] { "shared", "shared", "shared" }, await Task.WhenAll(first, second, third));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_finished_request_is_downloaded_again_next_time()
    {
        var (client, handler, _) = Create();
        await client.GetStringAsync("https://x/same");
        await client.GetStringAsync("https://x/same");
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Requests_are_spaced_by_the_interval()
    {
        var (client, _, time) = Create(minIntervalMs: 300);
        for (var i = 0; i < 5; i++) await client.GetStringAsync("https://x/" + i);
        Assert.True(time.TotalDelayMs >= 4 * 300, $"expected >= 1200 ms of spacing, got {time.TotalDelayMs}");
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_shared_download()
    {
        var (client, handler, _) = Create();
        handler.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Enqueue(HttpStatusCode.OK, "still delivered");
        using var cts = new CancellationTokenSource();

        var impatient = client.GetStringAsync("https://x/slow", cts.Token);
        var patient = client.GetStringAsync("https://x/slow");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);

        handler.Hold.SetResult();
        Assert.Equal("still delivered", await patient);
    }
}
