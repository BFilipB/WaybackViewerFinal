using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class PreloadCoordinatorTests
{
    private static (int Total, int Completed, int Failed, bool Running) Counts(PreloadStatus s) => (s.Total, s.Completed, s.Failed, s.Running);

    private static (PreloadCoordinator Coordinator, InMemoryArchiveStore Store, FakePageLoader Loader) Create(
        int workers = 4, int batch = 50, int maxDelayMs = 100, IArchiveStore? store = null)
    {
        var memory = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var options = Options.Create(new PreloadOptions { Workers = workers, WriteBatchSize = batch, WriteMaxDelayMs = maxDelayMs });
        var coordinator = new PreloadCoordinator(loader, store ?? memory, options, NullLogger<PreloadCoordinator>.Instance);
        return (coordinator, memory, loader);
    }

    private static async Task Idle(PreloadCoordinator c) => await c.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Downloads_everything_and_saves_in_batches()
    {
        var (coordinator, store, _) = Create(workers: 4, batch: 50);
        Assert.Equal(120, coordinator.Enqueue(SampleData.Stored(SampleData.Captures(120))));
        await Idle(coordinator);

        var status = coordinator.GetStatus();
        Assert.Equal((120, 120, 0, false), Counts(status));
        Assert.Equal(120, store.PageCount);
        Assert.Equal(120, store.SaveBatchSizes.Sum());
        Assert.True(store.SaveBatchSizes.Max() <= 50);
        Assert.True(store.SaveBatchSizes.Count < 60, "pages should be written in batches, not one by one");
    }

    [Fact]
    public async Task Reports_throughput_while_and_after_downloading()
    {
        var (coordinator, _, loader) = Create(workers: 2);
        loader.Hook = _ => Task.Delay(25);
        coordinator.Enqueue(SampleData.Stored(SampleData.Captures(40)));
        await Idle(coordinator);
        var status = coordinator.GetStatus();
        Assert.InRange(status.PerSecond, 5, 5000);
        Assert.Equal(status.PerSecond, coordinator.GetStatus().PerSecond); // frozen once the batch is finished
    }

    [Fact]
    public async Task Skips_saved_and_non_html_captures()
    {
        var (coordinator, _, loader) = Create();
        var list = new List<StoredCapture>
        {
            new(1, SampleData.Capture(1), Ready: true),
            new(2, SampleData.Capture(2, mime: "image/png"), Ready: false),
            new(3, SampleData.Capture(3), Ready: false)
        };
        Assert.Equal(1, coordinator.Enqueue(list));
        await Idle(coordinator);
        Assert.Equal(new[] { SampleData.Capture(3).Key }, loader.Started.ToArray());
    }

    [Fact]
    public async Task Loads_oldest_captures_first()
    {
        var (coordinator, _, loader) = Create(workers: 1);
        var shuffled = SampleData.Stored(SampleData.Captures(6)).Reverse().ToList();
        coordinator.Enqueue(shuffled);
        await Idle(coordinator);
        Assert.Equal(SampleData.Captures(6).Select(c => c.Key).ToArray(), loader.Started.ToArray());
    }

    [Fact]
    public async Task The_same_capture_is_never_queued_twice()
    {
        var (coordinator, _, loader) = Create(workers: 1);
        var gate = new TaskCompletionSource();
        loader.Hook = _ => gate.Task;
        var list = SampleData.Stored(SampleData.Captures(5));

        Assert.Equal(5, coordinator.Enqueue(list));
        Assert.Equal(0, coordinator.Enqueue(list));
        gate.SetResult();
        await Idle(coordinator);

        Assert.Equal(5, loader.Started.Count);
        Assert.Equal(5, coordinator.GetStatus().Total);
    }

    [Fact]
    public async Task Failures_are_counted_but_do_not_stop_the_rest()
    {
        var (coordinator, store, loader) = Create();
        var captures = SampleData.Captures(10);
        loader.FailKeys[captures[3].Key] = 1;
        loader.FailKeys[captures[7].Key] = 1;
        coordinator.Enqueue(SampleData.Stored(captures));
        await Idle(coordinator);

        Assert.Equal((10, 10, 2, false), Counts(coordinator.GetStatus()));
        Assert.Equal(8, store.PageCount);
    }

    [Fact]
    public async Task A_failing_database_write_is_reported_as_failures()
    {
        var (coordinator, _, _) = Create(store: new ThrowingSaveStore());
        coordinator.Enqueue(SampleData.Stored(SampleData.Captures(7)));
        await Idle(coordinator);
        Assert.Equal((7, 7, 7, false), Counts(coordinator.GetStatus()));
    }

    [Fact]
    public async Task Boost_moves_captures_to_the_front_of_the_queue()
    {
        var (coordinator, _, loader) = Create(workers: 1);
        var gate = new TaskCompletionSource();
        loader.Hook = _ => gate.Task;
        var captures = SampleData.Captures(5);
        coordinator.Enqueue(SampleData.Stored(captures));
        await SampleData.Eventually(() => loader.Started.Count == 1); // worker is busy with capture 0

        coordinator.Boost(new[] { captures[4].Key, captures[3].Key, "unknown|key" });
        gate.SetResult();
        await Idle(coordinator);

        Assert.Equal(new[] { captures[0], captures[4], captures[3], captures[1], captures[2] }.Select(c => c.Key).ToArray(),
            loader.Started.ToArray());
    }

    [Fact]
    public async Task MarkDone_removes_a_queued_capture()
    {
        var (coordinator, _, loader) = Create(workers: 1);
        var gate = new TaskCompletionSource();
        loader.Hook = _ => gate.Task;
        var captures = SampleData.Captures(4);
        coordinator.Enqueue(SampleData.Stored(captures));
        await SampleData.Eventually(() => loader.Started.Count == 1);

        coordinator.MarkDone(captures[3].Key);
        gate.SetResult();
        await Idle(coordinator);

        Assert.DoesNotContain(captures[3].Key, loader.Started);
        Assert.Equal((3, 3, 0, false), Counts(coordinator.GetStatus()));
    }

    [Fact]
    public async Task Counters_restart_for_a_new_batch()
    {
        var (coordinator, _, _) = Create();
        coordinator.Enqueue(SampleData.Stored(SampleData.Captures(3)));
        await Idle(coordinator);

        coordinator.Enqueue(SampleData.Stored(Enumerable.Range(10, 2).Select(i => SampleData.Capture(i))));
        await Idle(coordinator);

        Assert.Equal((2, 2, 0, false), Counts(coordinator.GetStatus()));
    }

    [Fact]
    public void Idle_from_the_start_and_nothing_to_do_for_empty_input()
    {
        var (coordinator, _, _) = Create();
        Assert.True(coordinator.WhenIdleAsync().IsCompleted);
        Assert.Equal(0, coordinator.Enqueue(Array.Empty<StoredCapture>()));
        Assert.False(coordinator.GetStatus().Running);
    }

    [Fact]
    public void Enqueue_after_dispose_does_nothing()
    {
        var (coordinator, _, _) = Create();
        coordinator.Dispose();
        Assert.Equal(0, coordinator.Enqueue(SampleData.Stored(SampleData.Captures(3))));
    }

    [Fact]
    public async Task Operation_updates_are_serialized_and_final_state_is_not_lost()
    {
        var store = new SlowOperationStore();
        var loader = new FakePageLoader { Hook = _ => Task.Delay(15) };
        var coordinator = new PreloadCoordinator(loader, store,
            Options.Create(new PreloadOptions { Workers = 2, WriteBatchSize = 10, WriteMaxDelayMs = 20 }),
            NullLogger<PreloadCoordinator>.Instance);

        var captures = SampleData.Stored(SampleData.Captures(8));
        coordinator.Enqueue(captures, "example.com/", "html200");
        await Task.Delay(25);
        coordinator.Pause("example.com/", "html200");
        coordinator.Resume("example.com/", "html200");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await SampleData.Eventually(async () =>
        {
            var latest = (await store.GetOperationsAsync())
                .Single(o => o.Prefix == "example.com/" && o.Filter == "html200" && o.Kind == OperationKind.PagePreload);
            return latest.State is OperationState.Completed or OperationState.Failed;
        }, timeoutMs: 5000);

        var op = (await store.GetOperationsAsync()).Single(o => o.Prefix == "example.com/" && o.Filter == "html200" && o.Kind == OperationKind.PagePreload);
        Assert.True(op.State is OperationState.Completed or OperationState.Failed);
        Assert.True(op.Completed + op.Failed >= op.Total);
        Assert.True(store.UpdateCalls > 0);
    }

    [Fact]
    public async Task StopAsync_waits_for_pending_save_batch_before_returning()
    {
        var store = new DelayedSaveStore();
        var loader = new FakePageLoader();
        var coordinator = new PreloadCoordinator(loader, store,
            Options.Create(new PreloadOptions { Workers = 1, WriteBatchSize = 50, WriteMaxDelayMs = 10 }),
            NullLogger<PreloadCoordinator>.Instance);

        coordinator.Enqueue(SampleData.Stored(SampleData.Captures(3)));
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stopping = coordinator.StopAsync(cts.Token);
        await Task.Delay(100); // should still be waiting because save is blocked
        Assert.False(stopping.IsCompleted);

        store.SaveGate.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class CaptureCatalogTests
{
    private sealed class Rig
    {
        public InMemoryArchiveStore Store = new();
        public FakeArchiveClient Archive = new();
        public FakeTimeSource Time = new();
        public CaptureCatalog Catalog;

        public Rig(int maxAgeDays = 30)
        {
            Catalog = new CaptureCatalog(Store, Archive, Options.Create(new StorageOptions { SearchMaxAgeDays = maxAgeDays }), Time);
            Archive.Route(ArchiveUrls.Cdx("example.com/", "html200"), SampleData.CdxJson(SampleData.Captures(5)));
        }
    }

    private static string Cdx(string filter = "html200") => ArchiveUrls.Cdx("example.com/", filter);

    [Fact]
    public async Task First_call_downloads_and_saves_then_database_serves_it()
    {
        var rig = new Rig();

        var first = await rig.Catalog.GetAsync("example.com/", "html200", false);
        Assert.False(first.FromDatabase);
        Assert.Equal(5, first.Snapshot.Captures.Count);

        var second = await rig.Catalog.GetAsync("example.com/", "html200", false);
        Assert.True(second.FromDatabase);
        Assert.Equal(5, second.Snapshot.Captures.Count);
        Assert.Equal(1, rig.Archive.Calls(Cdx()));
    }

    [Fact]
    public async Task Refresh_downloads_again()
    {
        var rig = new Rig();
        await rig.Catalog.GetAsync("example.com/", "html200", false);
        var refreshed = await rig.Catalog.GetAsync("example.com/", "html200", true);
        Assert.False(refreshed.FromDatabase);
        Assert.Equal(2, rig.Archive.Calls(Cdx()));
    }

    [Fact]
    public async Task Refresh_picks_up_new_captures_and_keeps_saved_pages()
    {
        var rig = new Rig();
        var first = await rig.Catalog.GetAsync("example.com/", "html200", false);
        await rig.Store.SavePagesAsync(new[]
        {
            new NewPage(first.Snapshot.Captures[0].Capture, HtmlCodec.Compress("<p>x</p>"), new ParsedPage(null, null, "x"), DateTimeOffset.UtcNow)
        });

        rig.Archive.Route(Cdx(), SampleData.CdxJson(SampleData.Captures(8)));
        var refreshed = await rig.Catalog.GetAsync("example.com/", "html200", true);

        Assert.Equal(8, refreshed.Snapshot.Captures.Count);
        Assert.Equal(1, refreshed.Snapshot.Captures.Count(c => c.Ready));
    }

    [Fact]
    public async Task Old_snapshots_are_refreshed()
    {
        var rig = new Rig(maxAgeDays: 30);
        await rig.Catalog.GetAsync("example.com/", "html200", false);
        rig.Time.Advance(TimeSpan.FromDays(31));
        var again = await rig.Catalog.GetAsync("example.com/", "html200", false);
        Assert.False(again.FromDatabase);
        Assert.Equal(2, rig.Archive.Calls(Cdx()));
    }

    [Fact]
    public async Task Snapshots_never_expire_when_max_age_is_zero()
    {
        var rig = new Rig(maxAgeDays: 0);
        await rig.Catalog.GetAsync("example.com/", "html200", false);
        rig.Time.Advance(TimeSpan.FromDays(5000));
        Assert.True((await rig.Catalog.GetAsync("example.com/", "html200", false)).FromDatabase);
        Assert.Equal(1, rig.Archive.Calls(Cdx()));
    }

    [Fact]
    public async Task Stale_copy_is_served_when_the_archive_is_unreachable()
    {
        var rig = new Rig();
        await rig.Catalog.GetAsync("example.com/", "html200", false);
        rig.Time.Advance(TimeSpan.FromDays(40));
        rig.Archive.Remove(Cdx());

        var result = await rig.Catalog.GetAsync("example.com/", "html200", false);
        Assert.True(result.FromDatabase);
        Assert.Equal(5, result.Snapshot.Captures.Count);
    }

    [Fact]
    public async Task Errors_surface_when_there_is_nothing_saved()
    {
        var rig = new Rig();
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Catalog.GetAsync("unknown.org/", "html200", false));
    }

    [Fact]
    public async Task Filters_are_saved_separately()
    {
        var rig = new Rig();
        rig.Archive.Route(Cdx("all"), SampleData.CdxJson(SampleData.Captures(9)));
        var html = await rig.Catalog.GetAsync("example.com/", "html200", false);
        var all = await rig.Catalog.GetAsync("example.com/", "all", false);
        Assert.Equal(5, html.Snapshot.Captures.Count);
        Assert.Equal(9, all.Snapshot.Captures.Count);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_download()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Archive.BeforeResponse = _ => gate.Task;

        var tasks = Enumerable.Range(0, 5).Select(_ => rig.Catalog.GetAsync("example.com/", "html200", false)).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(1, rig.Archive.Calls(Cdx()));
    }
}

public class PageServiceTests
{
    private static (PageService Service, InMemoryArchiveStore Store, FakePageLoader Loader, PreloadCoordinator Preload) Create()
    {
        var store = new InMemoryArchiveStore();
        var loader = new FakePageLoader();
        var preload = new PreloadCoordinator(loader, store, Options.Create(new PreloadOptions { Workers = 1 }), NullLogger<PreloadCoordinator>.Instance);
        return (new PageService(store, loader, preload), store, loader, preload);
    }

    [Fact]
    public async Task Saved_pages_are_returned_without_downloading()
    {
        var (service, store, loader, _) = Create();
        var capture = SampleData.Capture(1);
        var html = SampleData.Html("Saved", "body");
        await store.SavePagesAsync(new[] { new NewPage(capture, HtmlCodec.Compress(html), new ParsedPage("Saved", null, "body"), DateTimeOffset.UtcNow) });

        var page = await service.GetAsync(capture.Timestamp, capture.Original, includeHtml: true);

        Assert.Equal("Saved", page!.Parsed.Title);
        Assert.Equal(html, HtmlCodec.Decompress(page.HtmlGz!));
        Assert.Empty(loader.Started);
    }

    [Fact]
    public async Task Missing_pages_are_downloaded_once_and_saved()
    {
        var (service, store, loader, _) = Create();
        var capture = SampleData.Capture(2);

        var first = await service.GetAsync(capture.Timestamp, capture.Original, includeHtml: false);
        var second = await service.GetAsync(capture.Timestamp, capture.Original, includeHtml: true);

        Assert.NotNull(first);
        Assert.Null(first!.HtmlGz);
        Assert.NotNull(second!.HtmlGz);
        Assert.Single(loader.Started);
        Assert.Equal(1, store.PageCount);
    }

    [Fact]
    public async Task Download_failures_return_null()
    {
        var (service, store, loader, _) = Create();
        var capture = SampleData.Capture(3);
        loader.FailKeys[capture.Key] = 1;

        Assert.Null(await service.GetAsync(capture.Timestamp, capture.Original, includeHtml: true));
        Assert.Equal(0, store.PageCount);
    }

    [Fact]
    public async Task On_demand_download_removes_the_capture_from_the_background_queue()
    {
        var (service, _, loader, preload) = Create();
        var gate = new TaskCompletionSource();
        var captures = SampleData.Captures(3);
        loader.Hook = c => c.Key == captures[0].Key ? gate.Task : Task.CompletedTask;
        preload.Enqueue(SampleData.Stored(captures));
        await SampleData.Eventually(() => loader.Started.Count == 1);

        await service.GetAsync(captures[2].Timestamp, captures[2].Original, includeHtml: false);
        gate.SetResult();
        await preload.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, loader.Started.Count(k => k == captures[2].Key)); // only the on-demand load
    }
}
