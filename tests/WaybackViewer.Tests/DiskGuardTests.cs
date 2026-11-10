using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class DiskGuardTests
{
    private static List<StoredCapture> Job(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new StoredCapture(i, new Capture($"2020010100{i:0000}", $"https://g.com/p{i}", "text/html", "200"), false))
            .ToList();

    [Fact]
    public async Task Low_disk_space_pauses_preload_without_downloading_and_resume_completes_it()
    {
        var loader = new FakePageLoader();
        var store = new InMemoryArchiveStore();
        using var coordinator = new PreloadCoordinator(loader, store,
            Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10, MinFreeDiskMb = 100 }),
            NullLogger<PreloadCoordinator>.Instance);
        long free = 10L * 1024 * 1024;
        coordinator.FreeDiskBytes = () => free;

        await coordinator.EnqueueAsync(Job(3), "g.com/", "html200");
        await SampleData.Eventually(async () => (await store.GetOperationsAsync()).Any(o => o.State == OperationState.Paused));
        Assert.Empty(loader.Started);

        free = 10L * 1024 * 1024 * 1024;
        await Task.Delay(5_100);
        coordinator.Resume("g.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, loader.Started.Count);
    }

    [Fact]
    public async Task Guard_off_never_pauses()
    {
        var loader = new FakePageLoader();
        using var coordinator = new PreloadCoordinator(loader, new InMemoryArchiveStore(),
            Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10, MinFreeDiskMb = 0 }),
            NullLogger<PreloadCoordinator>.Instance);
        coordinator.FreeDiskBytes = () => 0;
        await coordinator.EnqueueAsync(Job(2), "g.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, loader.Started.Count);
    }
}