using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class HtmlPayloadFileStoreTests
{
    [Fact]
    public void Path_is_grouped_by_host_year_month_day()
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-html-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new HtmlFileStore(root);
            var capture = new Capture("20240203112233", "https://news.example.com/a/b", "text/html", "200");

            var path = files.PathFor(capture);
            Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("news.example.com", "2024", "02", "03"), path, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".gz", path, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Sqlite_store_reads_legacy_blob_when_file_payload_is_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-html-legacy-" + Guid.NewGuid().ToString("N"));
        var htmlRoot = Path.Combine(dir, "payloads");
        try
        {
            var db = Path.Combine(dir, "test.db");
            var store = new SqliteArchiveStore($"Data Source={db}", new HtmlFileStore(htmlRoot));
            await store.InitializeAsync();

            var capture = new Capture("20260102030405", "https://legacy.example.com/path", "text/html", "200");
            var html = HtmlCodec.Compress("<p>legacy-db-blob</p>");
            await store.SavePagesAsync([new NewPage(capture, html, new ParsedPage("T", null, "x"), DateTimeOffset.UtcNow)]);

            var payloadPath = new HtmlFileStore(htmlRoot).PathFor(capture);
            if (File.Exists(payloadPath)) File.Delete(payloadPath);

            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    UPDATE PageHtml
                    SET HtmlGz = $h
                    WHERE CaptureId = (
                        SELECT Id FROM Captures WHERE CaptureTs = $ts AND Original = $o
                    )";
                cmd.Parameters.AddWithValue("$h", html);
                cmd.Parameters.AddWithValue("$ts", capture.Timestamp);
                cmd.Parameters.AddWithValue("$o", capture.Original);
                await cmd.ExecuteNonQueryAsync();
            }

            var loaded = await store.GetPageAsync(capture.Timestamp, capture.Original, includeHtml: true);
            Assert.NotNull(loaded?.HtmlGz);
            Assert.Equal("<p>legacy-db-blob</p>", HtmlCodec.Decompress(loaded!.HtmlGz!));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task In_memory_store_writes_and_reads_payload_from_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-html-mem-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemoryArchiveStore(new HtmlFileStore(root));
            var capture = SampleData.Capture(7);
            var html = HtmlCodec.Compress("<p>payload-from-file</p>");

            await store.SavePagesAsync([new NewPage(capture, html, new ParsedPage("T", null, "payload"), DateTimeOffset.UtcNow)]);
            var loaded = await store.GetPageAsync(capture.Timestamp, capture.Original, includeHtml: true);

            Assert.NotNull(loaded?.HtmlGz);
            Assert.Equal("<p>payload-from-file</p>", HtmlCodec.Decompress(loaded!.HtmlGz!));
            Assert.True(Directory.EnumerateFiles(root, "*.gz", SearchOption.AllDirectories).Any());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task Sqlite_store_writes_payload_files_in_host_date_layout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-html-sql-" + Guid.NewGuid().ToString("N"));
        var htmlRoot = Path.Combine(dir, "payloads");
        try
        {
            var db = Path.Combine(dir, "test.db");
            var store = new SqliteArchiveStore($"Data Source={db}", new HtmlFileStore(htmlRoot));
            await store.InitializeAsync();

            var capture = new Capture("20250102030405", "https://x.example.com/path", "text/html", "200");
            await store.SavePagesAsync([new NewPage(capture, HtmlCodec.Compress("<p>sqlite-file</p>"), new ParsedPage("T", null, "x"), DateTimeOffset.UtcNow)]);

            var loaded = await store.GetPageAsync(capture.Timestamp, capture.Original, includeHtml: true);
            Assert.Equal("<p>sqlite-file</p>", HtmlCodec.Decompress(loaded!.HtmlGz!));

            var expectedFolder = Path.Combine(htmlRoot, "x.example.com", "2025", "01", "02");
            Assert.True(Directory.Exists(expectedFolder));
            Assert.True(Directory.EnumerateFiles(expectedFolder, "*.gz", SearchOption.TopDirectoryOnly).Any());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
