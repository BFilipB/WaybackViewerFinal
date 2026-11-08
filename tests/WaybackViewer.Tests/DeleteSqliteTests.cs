using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class DeleteSqliteTests
{
    private const string A = "a.com/";
    private const string B = "b.com/";
    private const string F = "html200";

    private static NewPage Page(Capture c)
    {
        var html = SampleData.Html(c.Original, "x" + c.Timestamp);
        return new NewPage(c, HtmlCodec.Compress(html), PageParser.Parse(html), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Sqlite_delete_keeps_shared_captures_and_removes_exclusive_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-dels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var files = new HtmlFileStore(Path.Combine(dir, "html"));
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", files);
        await store.InitializeAsync();

        var a = SampleData.Captures(6);                    // 0..5
        var b = SampleData.Captures(9).Skip(4).ToList();   // 4..8; 4 and 5 shared
        await store.SaveSearchAsync(A, F, a, DateTimeOffset.UtcNow);
        await store.SaveSearchAsync(B, F, b, DateTimeOffset.UtcNow);
        await store.SavePagesAsync(a.Concat(b.Skip(2)).Select(Page).ToList());

        var preview = await store.GetDeletePreviewAsync(A, F);
        Assert.Equal(6, preview.Captures);
        Assert.Equal(2, preview.SharedCaptures);
        Assert.Equal(4, preview.Pages);
        Assert.True(preview.Bytes > 0);

        var removed = 0;
        int n;
        while ((n = await store.DeletePagesBatchAsync(A, F, 3)) > 0) { Assert.True(n <= 3); removed += n; }
        Assert.Equal(4, removed);
        Assert.False(files.Exists(a[0]));
        Assert.True(files.Exists(a[4]));                    // shared with B: file stays
        Assert.Equal(6, (await store.GetSearchAsync(A, F))!.Captures.Count);

        while (await store.DeleteCapturesBatchAsync(A, F, 2) > 0) { }
        await store.DeleteSearchAsync(A, F);
        Assert.Null(await store.GetSearchAsync(A, F));
        Assert.Equal(5, (await store.GetSearchAsync(B, F))!.Captures.Count);
        Assert.True(files.Exists(a[4]));
    }
}
