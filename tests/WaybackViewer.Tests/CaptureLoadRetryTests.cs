using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class CaptureLoadRetryTests
{
    private static (CaptureLoadRunner Runner, PreloadCoordinator Preload, InMemoryArchiveStore Store, FakeArchiveClient Archive, FakeTimeSource Time) Make(int retries)
    {
        var archive = new FakeArchiveClient();
        var store = new InMemoryArchiveStore();
        var time = new FakeTimeSource();
        var catalog = new CaptureCatalog(store, archive, Options.Create(new StorageOptions()), time);
        var preload = new PreloadCoordinator(new FakePageLoader(), store, Options.Create(new PreloadOptions { Workers = 1, WriteMaxDelayMs = 10 }),
            NullLogger<PreloadCoordinator>.Instance);
        var runner = new CaptureLoadRunner(catalog, preload, store, NullLogger<CaptureLoadRunner>.Instance, time,
            Options.Create(new ArchiveOptions { LoadRetries = retries, LoadRetryDelaySeconds = 20 }));
        return (runner, preload, store, archive, time);
    }

    [Fact]
    public async Task Transient_cdx_failure_is_retried_with_backoff_and_then_succeeds()
    {
        var (runner, preload, store, archive, time) = Make(retries: 3);
        using var _ = preload;
        var url = ArchiveUrls.Cdx("a.com/", "html200");
        var calls = 0;
        archive.Route(url, () => ++calls < 3
            ? throw new ArchiveRequestException(System.Net.HttpStatusCode.GatewayTimeout, null)
            : SampleData.CdxJson(SampleData.Captures(3)));

        runner.Start("a.com/", "html200", refresh: false);
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await preload.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, archive.Calls(url));
        Assert.Contains(TimeSpan.FromSeconds(20), time.Delays);
        Assert.Contains(TimeSpan.FromSeconds(40), time.Delays);
        var ops = await ((IArchiveStore)store).GetOperationsForSearchAsync("a.com/", "html200");
        Assert.Equal(OperationState.Completed, ops.Single(o => o.Kind == OperationKind.CaptureLoad).State);
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried_and_is_recorded_as_failed()
    {
        var (runner, preload, store, archive, time) = Make(retries: 3);
        using var _ = preload;
        var url = ArchiveUrls.Cdx("a.com/", "html200");
        archive.Route(url, () => throw new ArchiveRequestException(System.Net.HttpStatusCode.Forbidden, null));

        runner.Start("a.com/", "html200", refresh: false);
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, archive.Calls(url));
        Assert.Empty(time.Delays);
        var ops = await ((IArchiveStore)store).GetOperationsForSearchAsync("a.com/", "html200");
        Assert.Equal(OperationState.Failed, ops.Single(o => o.Kind == OperationKind.CaptureLoad).State);
    }

    [Fact]
    public async Task Failure_after_all_retries_is_recorded_not_swallowed()
    {
        var (runner, preload, store, archive, _) = Make(retries: 2);
        using var _p = preload;
        var url = ArchiveUrls.Cdx("a.com/", "html200");
        archive.Route(url, () => throw new HttpRequestException("reset"));

        runner.Start("a.com/", "html200", refresh: false);
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, archive.Calls(url));   // 1 + 2 retries
        var ops = await ((IArchiveStore)store).GetOperationsForSearchAsync("a.com/", "html200");
        var op = ops.Single(o => o.Kind == OperationKind.CaptureLoad);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.False(string.IsNullOrEmpty(op.Error));
    }
}
