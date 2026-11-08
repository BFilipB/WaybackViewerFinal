using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class DeleteStoreTests
{
    private const string A = "a.com/";
    private const string B = "b.com/";
    private const string F = "html200";

    private static NewPage Page(Capture c)
    {
        var html = SampleData.Html(c.Original, "x" + c.Timestamp);
        return new NewPage(c, HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow);
    }

    private static async Task<(InMemoryArchiveStore Store, HtmlFileStore Files)> SeedAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-del-" + Guid.NewGuid().ToString("N"));
        var files = new HtmlFileStore(dir);
        var store = new InMemoryArchiveStore(files);
        var a = SampleData.Captures(6);                   // 0..5
        var b = SampleData.Captures(9).Skip(4).ToList();  // 4..8, 4 and 5 shared with A
        await store.SaveSearchAsync(A, F, a, DateTimeOffset.UtcNow);
        await store.SaveSearchAsync(B, F, b, DateTimeOffset.UtcNow);
        await store.SavePagesAsync(a.Concat(b.Skip(2)).Select(Page).ToList());
        return (store, files);
    }

    [Fact]
    public async Task Preview_counts_shared_captures()
    {
        var (store, _) = await SeedAsync();
        var p = await store.GetDeletePreviewAsync(A, F);
        Assert.Equal(6, p.Captures);
        Assert.Equal(2, p.SharedCaptures);
        Assert.Equal(4, p.Pages);
        Assert.True(p.Bytes > 0);
    }

    [Fact]
    public async Task Delete_pages_batches_and_keeps_shared_files()
    {
        var (store, files) = await SeedAsync();
        var total = 0;
        while (true)
        {
            var n = await store.DeletePagesBatchAsync(A, F, 3);
            if (n == 0) break;
            Assert.True(n <= 3);
            total += n;
        }
        Assert.Equal(4, total);
        Assert.False(files.Exists(SampleData.Capture(0)));
        Assert.True(files.Exists(SampleData.Capture(4)));
        Assert.Equal(6, (await store.GetSearchAsync(A, F))!.Captures.Count);
    }

    [Fact]
    public async Task Delete_all_keeps_other_urls()
    {
        var (store, files) = await SeedAsync();
        while (await store.DeletePagesBatchAsync(A, F, 2) > 0) { }
        while (await store.DeleteCapturesBatchAsync(A, F, 2) > 0) { }
        await store.DeleteSearchAsync(A, F);
        Assert.Null(await store.GetSearchAsync(A, F));
        Assert.Equal(5, (await store.GetSearchAsync(B, F))!.Captures.Count);
        Assert.True(files.Exists(SampleData.Capture(4)));
    }

    [Fact]
    public async Task Delete_operations_removes_only_requested_kinds()
    {
        var (store, _) = await SeedAsync();
        await store.CreateOrGetOperationAsync(A, F, OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
        await store.CreateOrGetOperationAsync(A, F, OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.DeleteOperationsAsync(A, F, [OperationKind.PagePreload]);
        Assert.Equal(OperationKind.CaptureLoad, Assert.Single(await ((IArchiveStore)store).GetOperationsForSearchAsync(A, F)).Kind);
    }
}
