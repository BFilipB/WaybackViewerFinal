using Microsoft.Data.Sqlite;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class ExtractorVersionTests
{
    private const string Prefix = "a.com/";
    private const string Filter = "html200";
    private const string Html =
        "<html><head><script type=\"application/ld+json\">{\"@type\":\"VideoObject\",\"viewCount\":777}</script></head><body>x</body></html>";

    private static async Task<(SqliteArchiveStore Store, PageService Service, List<Capture> Caps, string Db)> Seed(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-ev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "d.db");
        var store = new SqliteArchiveStore($"Data Source={db}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        var caps = SampleData.Captures(count);
        await store.SaveSearchAsync(Prefix, Filter, caps, DateTimeOffset.UtcNow);
        // Saved with an old extractor: metadata present with a stale view count.
        var pages = caps.Select(c => new NewPage(c, HtmlCodec.Compress(Html),
            new ParsedPage(null, null, "x", new PageMetadata(Views: 5, Likes: 1, Confidence: 0.5)), DateTimeOffset.UtcNow)).ToList();
        await store.SavePagesAsync(pages);
        return (store, new PageService(store, new FakePageLoader(), null!), caps, db);
    }

    private static void SetVersion(string db, string tsOriginalKeyTs, object? version)
    {
        using var conn = new SqliteConnection($"Data Source={db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE PageMetadata SET ExtractorVersion = $v WHERE CaptureId IN (SELECT Id FROM Captures WHERE CaptureTs = $ts)";
        cmd.Parameters.AddWithValue("$v", version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", tsOriginalKeyTs);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Old_version_rows_are_refreshed_from_saved_html_and_current_rows_are_untouched()
    {
        var (store, service, caps, db) = await Seed(2);
        SetVersion(db, caps[0].Timestamp, null);   // unversioned (older extractor)

        Assert.Single(await store.GetPagesWithStaleExtractorAsync(Prefix, Filter, ExtractorVersion.Current, 0, 10));
        Assert.Equal(1, await service.ReprocessStaleAsync(Prefix, Filter));

        var refreshed = await store.GetPageAsync(caps[0].Timestamp, caps[0].Original, false);
        var untouched = await store.GetPageAsync(caps[1].Timestamp, caps[1].Original, false);
        Assert.Equal(777, refreshed!.Parsed.Metadata!.Views);
        Assert.Equal(5, untouched!.Parsed.Metadata!.Views);
    }

    [Fact]
    public async Task Reprocess_is_repeatable_and_resumes_with_only_the_remaining_rows()
    {
        var (store, service, caps, db) = await Seed(3);
        foreach (var c in caps) SetVersion(db, c.Timestamp, 1);

        // First pass is interrupted before it starts: nothing is lost, state lives in the database.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReprocessStaleAsync(Prefix, Filter, cts.Token));
        Assert.Equal(3, (await store.GetPagesWithStaleExtractorAsync(Prefix, Filter, ExtractorVersion.Current, 0, 10)).Count);

        Assert.Equal(3, await service.ReprocessStaleAsync(Prefix, Filter));
        Assert.Equal(0, await service.ReprocessStaleAsync(Prefix, Filter));
    }

    [Fact]
    public async Task Rows_without_saved_html_are_skipped_not_failed()
    {
        var (store, service, caps, db) = await Seed(1);
        SetVersion(db, caps[0].Timestamp, null);
        var dir = Path.GetDirectoryName(db)!;
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "html"), "*", SearchOption.AllDirectories)) File.Delete(f);

        Assert.Equal(0, await service.ReprocessStaleAsync(Prefix, Filter));
    }

    [Fact]
    public async Task Reprocess_rewrites_only_metadata_not_payload_files_or_text()
    {
        var (store, service, caps, db) = await Seed(2);
        foreach (var c in caps) SetVersion(db, c.Timestamp, null);
        var htmlDir = Path.Combine(Path.GetDirectoryName(db)!, "html");
        var before = Directory.GetFiles(htmlDir, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));

        Assert.Equal(2, await service.ReprocessStaleAsync(Prefix, Filter));

        foreach (var (path, stamp) in before) Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        var page = await store.GetPageAsync(caps[0].Timestamp, caps[0].Original, false);
        Assert.Equal("x", page!.Parsed.Text);
        Assert.Equal(777, page.Parsed.Metadata!.Views);
    }
}
