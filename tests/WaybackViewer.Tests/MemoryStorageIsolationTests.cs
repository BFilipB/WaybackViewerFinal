using System.Net;
using Microsoft.Extensions.Configuration;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class MemoryStorageIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_memory_store_round_trips_and_deletes_without_a_data_drive(bool useProvider)
    {
        var store = useProvider
            ? (InMemoryArchiveStore)new InMemoryArchiveStoreProvider().Create(new ConfigurationBuilder().Build())
            : new InMemoryArchiveStore();
        var capture = SampleData.Capture(7);
        var html = HtmlCodec.Compress("<p>memory-only</p>");
        await store.InitializeAsync();
        await store.SaveSearchAsync("example.com/", "html200", [capture], DateTimeOffset.UtcNow);
        await store.SavePagesAsync([new NewPage(capture, html, new ParsedPage("T", null, "memory-only"), DateTimeOffset.UtcNow)]);

        var loaded = await store.GetPageAsync(capture.Timestamp, capture.Original, includeHtml: true);
        Assert.Equal(html, loaded!.HtmlGz);
        Assert.Null((await store.GetPageAsync(capture.Timestamp, capture.Original, includeHtml: false))!.HtmlGz);
        var preview = await store.GetDeletePreviewAsync("example.com/", "html200");
        Assert.Equal(html.LongLength, preview.Bytes);
        Assert.Equal(1, await store.DeletePagesBatchAsync("example.com/", "html200", 10));
        Assert.Null(await store.GetPageAsync(capture.Timestamp, capture.Original, includeHtml: true));
    }

    [Fact]
    public async Task Explicit_memory_payload_directory_is_honored()
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-memory-provider-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:HtmlRoot"] = root
            }).Build();
            var store = new InMemoryArchiveStoreProvider().Create(config);
            var capture = SampleData.Capture(7);
            var html = HtmlCodec.Compress("<p>explicit-files</p>");
            await store.SavePagesAsync([new NewPage(capture, html, new ParsedPage("T", null, "x"), DateTimeOffset.UtcNow)]);

            var files = new HtmlFileStore(root);
            Assert.Equal(html, await files.LoadAsync(capture));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Memory_payload_operations_honor_cancellation()
    {
        var store = new InMemoryArchiveStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SavePagesAsync([], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPageAsync("ts", "url", true, cts.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Memory_host_is_healthy_with_isolated_payload_configuration(bool useFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-memory-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await AppHost.StartAsync(new Dictionary<string, string?>
            {
                ["Storage:HtmlRoot"] = useFiles ? root : ""
            });
            using var response = await host.Client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, host.Archive.TotalCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
