using System.Text;
using System.Text.Json;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class LegacyCacheImporterTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wb-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyCacheImporterTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    // Same shape the v1 file cache wrote: UTF-8 with BOM, {"Value": "...", "ExpiresUtc": "..."}
    private void WriteEntry(string key, string value)
    {
        var json = JsonSerializer.Serialize(new { Value = value, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) });
        File.WriteAllText(Path.Combine(dir, LegacyCacheImporter.FileNameFor(key)), json, new UTF8Encoding(true));
    }

    private void WriteList(IEnumerable<Capture> captures)
    {
        var list = captures.Select(c => new { c.Timestamp, c.Original, c.MimeType, c.StatusCode, c.ArchiveUrl, c.FrameUrl, c.Date });
        WriteEntry("captures|html200|whatever", JsonSerializer.Serialize(new { Captures = list, CachedAt = DateTimeOffset.UtcNow }));
    }

    [Fact]
    public async Task Imports_pages_that_match_a_saved_capture_list()
    {
        var caps = SampleData.Captures(4);
        WriteList(caps);
        WriteEntry("html|" + caps[0].Key, SampleData.Html("First", "one"));
        WriteEntry("html|" + caps[1].Key, SampleData.Html("Second", "two"));
        // caps[2]: no cached file at all
        WriteEntry("html|" + caps[3].Key, "\u001f\uFFFD\b\0\0\0\0\0\0\0<\uFFFD\uFFFD\u000e\uFFFD0"); // corrupt gzip bytes stored as text

        var store = new InMemoryArchiveStore();
        var report = await new LegacyCacheImporter(store).ImportAsync(dir);

        Assert.Equal(new ImportReport(Files: 4, CaptureLists: 1, Captures: 4, Imported: 2, Invalid: 1, Missing: 1), report);

        var first = await store.GetPageAsync(caps[0].Timestamp, caps[0].Original, includeHtml: true);
        Assert.Equal("First", first!.Parsed.Title);
        Assert.Contains("one", first.Parsed.Text);
        Assert.Contains("<title>First</title>", HtmlCodec.Decompress(first.HtmlGz!));
        Assert.Null(await store.GetPageAsync(caps[2].Timestamp, caps[2].Original, false));
        Assert.Null(await store.GetPageAsync(caps[3].Timestamp, caps[3].Original, false));
    }

    [Fact]
    public async Task Imported_pages_make_a_later_search_report_them_as_ready()
    {
        var caps = SampleData.Captures(3);
        WriteList(caps);
        foreach (var c in caps) WriteEntry("html|" + c.Key, SampleData.Html("P", "x"));

        var store = new InMemoryArchiveStore();
        await new LegacyCacheImporter(store).ImportAsync(dir);
        var snapshot = await store.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);

        Assert.All(snapshot.Captures, c => Assert.True(c.Ready));
    }

    [Fact]
    public async Task Handles_more_pages_than_one_batch()
    {
        var caps = SampleData.Captures(120);
        WriteList(caps);
        foreach (var c in caps) WriteEntry("html|" + c.Key, SampleData.Html(c.Original, "x"));

        var store = new InMemoryArchiveStore();
        var report = await new LegacyCacheImporter(store).ImportAsync(dir);

        Assert.Equal(120, report.Imported);
        Assert.Equal(120, store.PageCount);
    }

    [Fact]
    public async Task Folder_without_capture_lists_imports_nothing()
    {
        WriteEntry("html|20200101000000|https://a.com/", SampleData.Html("Orphan", "x"));
        var report = await new LegacyCacheImporter(new InMemoryArchiveStore()).ImportAsync(dir);
        Assert.Equal(0, report.Imported);
        Assert.Equal(0, report.CaptureLists);
    }

    [Fact]
    public async Task Missing_folder_gives_a_clear_error() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new LegacyCacheImporter(new InMemoryArchiveStore()).ImportAsync(Path.Combine(dir, "nope")));

    [Fact]
    public void File_names_match_the_old_cache_scheme() =>
        // sha256("html|20200101000000|https://a.com/") computed independently of the importer
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("html|20200101000000|https://a.com/"))).ToLowerInvariant() + ".json",
            LegacyCacheImporter.FileNameFor("html|20200101000000|https://a.com/"));
}
