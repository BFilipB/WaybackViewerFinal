using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// The coordinator keeps its hot per-capture bookkeeping (retry counters, unsaved counter) off the queue lock.
public class PreloadContentionTests
{
    private static List<StoredCapture> Job(string site, int count) =>
        Enumerable.Range(1, count)
            .Select(i => new StoredCapture(i, new Capture($"2020010100{i:0000}", $"https://{site}/p{i}", "text/html", "200"), false))
            .ToList();

    private static PreloadCoordinator Make(FakePageLoader loader, int retryDelayMs = 1) =>
        new(loader, new InMemoryArchiveStore(),
            Options.Create(new PreloadOptions { Workers = 8, WriteMaxDelayMs = 5, RetryBaseDelayMs = retryDelayMs, RetryMaxDelayMs = retryDelayMs, MaxAttempts = 5 }),
            NullLogger<PreloadCoordinator>.Instance);

    [Fact]
    public async Task Concurrent_enqueues_from_many_requests_complete_with_exact_accounting()
    {
        var loader = new FakePageLoader();
        using var coordinator = Make(loader);

        var sites = Enumerable.Range(0, 24).Select(i => $"s{i}.com").ToArray();
        await Task.WhenAll(sites.Select(s => Task.Run(() => coordinator.EnqueueAsync(Job(s, 150), s + "/", "html200"))));
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(sites.Length * 150, loader.Started.Distinct().Count());
        var status = coordinator.GetStatus();
        Assert.Equal(status.Total, status.Completed);
        Assert.Equal(0, status.Failed);
    }

    [Fact]
    public async Task Transient_failures_retry_per_capture_and_every_page_ends_up_saved()
    {
        var loader = new FakePageLoader();
        var tries = new ConcurrentDictionary<string, int>();
        loader.Hook = c =>
            tries.AddOrUpdate(c.Key, 1, (_, n) => n + 1) <= 2
                ? Task.FromException(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests))
                : Task.CompletedTask;
        using var coordinator = Make(loader);

        await coordinator.EnqueueAsync(Job("retry.com", 200), "retry.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(200, tries.Count);
        Assert.All(tries.Values, n => Assert.Equal(3, n));
        var status = coordinator.GetStatus();
        Assert.Equal(0, status.Failed);
        Assert.Equal(200, status.Completed);
    }

    [Fact]
    public async Task Heavy_status_polling_does_not_stall_workers_or_skew_accounting()
    {
        var loader = new FakePageLoader();
        using var coordinator = Make(loader);
        using var stop = new CancellationTokenSource();
        var polls = 0;
        var pollers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) { coordinator.GetStatus(); Interlocked.Increment(ref polls); }
        })).ToArray();

        var sites = Enumerable.Range(0, 10).Select(i => $"p{i}.com").ToArray();
        await Task.WhenAll(sites.Select(s => Task.Run(() => coordinator.EnqueueAsync(Job(s, 200), s + "/", "html200"))));
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
        stop.Cancel();
        await Task.WhenAll(pollers);

        var status = coordinator.GetStatus();
        Assert.Equal(2000, status.Completed);
        Assert.Equal(status.Total, status.Completed);
        Assert.Equal(0, status.Failed);
        Assert.True(polls > 0);
    }}
