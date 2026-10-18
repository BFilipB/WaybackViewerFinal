using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class SchedulerTests
{
    private static (PreloadCoordinator Coordinator, FakePageLoader Loader) Create(int workers)
    {
        var loader = new FakePageLoader();
        var options = Options.Create(new PreloadOptions { Workers = workers, WriteBatchSize = 50, WriteMaxDelayMs = 50 });
        return (new PreloadCoordinator(loader, new InMemoryArchiveStore(), options, NullLogger<PreloadCoordinator>.Instance), loader);
    }

    private static List<StoredCapture> Job(string site, int count) =>
        Enumerable.Range(1, count)
            .Select(i => new StoredCapture(i, new Capture($"2020010100{i:0000}", $"https://{site}/p{i}", "text/html", "200"), false))
            .ToList();

    [Fact]
    public async Task Two_jobs_are_served_alternately()
    {
        var (coordinator, loader) = Create(workers: 1);
        coordinator.Enqueue(Job("a.com", 4), "a.com/", "html200");
        coordinator.Enqueue(Job("b.com", 4), "b.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var order = loader.Started.ToArray();
        Assert.Equal(8, order.Length);
        for (var i = 1; i < order.Length; i++)
            Assert.NotEqual(order[i - 1].Contains("a.com"), order[i].Contains("a.com"));
    }

    [Fact]
    public async Task A_paused_job_does_not_block_other_jobs_and_resumes_later()
    {
        var (coordinator, loader) = Create(workers: 1);
        var gate = new TaskCompletionSource();
        loader.Hook = _ => gate.Task;
        coordinator.Enqueue(Job("a.com", 3), "a.com/", "html200");
        coordinator.Enqueue(Job("b.com", 3), "b.com/", "html200");
        coordinator.Pause("a.com/", "html200");
        var startedBeforeRelease = loader.Started.ToArray().Count(k => k.Contains("a.com"));
        gate.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (loader.Started.ToArray().Count(k => k.Contains("b.com")) < 3 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(200);
        Assert.Equal(3, loader.Started.ToArray().Count(k => k.Contains("b.com")));
        Assert.True(loader.Started.ToArray().Count(k => k.Contains("a.com")) <= startedBeforeRelease + 1, "paused job must not keep being scheduled");

        coordinator.Resume("a.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(6, loader.Started.Count);
    }

    [Fact]
    public async Task Stopping_a_job_discards_only_its_queue()
    {
        var (coordinator, loader) = Create(workers: 1);
        var gate = new TaskCompletionSource();
        loader.Hook = _ => gate.Task;
        coordinator.Enqueue(Job("a.com", 5), "a.com/", "html200");
        coordinator.Enqueue(Job("b.com", 3), "b.com/", "html200");
        coordinator.Stop("a.com/", "html200");
        gate.SetResult();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, loader.Started.ToArray().Count(k => k.Contains("b.com")));
        Assert.True(loader.Started.ToArray().Count(k => k.Contains("a.com")) <= 1);
    }
}

public class ParallelJobsEndpointTests
{
    [Fact]
    public async Task Two_start_search_calls_create_independent_active_operations()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("a.com/", SampleData.Captures(3));
        host.RouteSite("b.com/", SampleData.Captures(3));

        using var aContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = "a.com/",
            ["filter"] = "html200",
            ["refresh"] = "false"
        });
        using var bContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = "b.com/",
            ["filter"] = "html200",
            ["refresh"] = "false"
        });

        var startA = await host.Client.PostAsync("/Home/StartSearch", aContent);
        var startB = await host.Client.PostAsync("/Home/StartSearch", bContent);
        Assert.True(startA.IsSuccessStatusCode);
        Assert.True(startB.IsSuccessStatusCode);

        using var startADoc = JsonDocument.Parse(await startA.Content.ReadAsStringAsync());
        using var startBDoc = JsonDocument.Parse(await startB.Content.ReadAsStringAsync());
        Assert.Equal("url", startADoc.RootElement.GetProperty("searchMode").GetString());
        Assert.Equal("url", startBDoc.RootElement.GetProperty("searchMode").GetString());

        await SampleData.Eventually(async () =>
        {
            using var doc = JsonDocument.Parse(await host.GetAsync("/Home/OperationStatus"));
            var ops = doc.RootElement.EnumerateArray().ToArray();
            var expectedKind = (int)OperationKind.CaptureLoad;
            var a = ops.Any(o => o.GetProperty("prefix").GetString() == "a.com/" &&
                                 o.GetProperty("kind").GetInt32() == expectedKind);
            var b = ops.Any(o => o.GetProperty("prefix").GetString() == "b.com/" &&
                                 o.GetProperty("kind").GetInt32() == expectedKind);
            return a && b;
        });
    }
}

public class HealthEndpointTests
{
    [Fact]
    public async Task Healthz_reports_ok()
    {
        await using var host = await AppHost.StartAsync();
        var body = await host.GetAsync("/healthz");
        Assert.Contains("ok", body);
    }
}
