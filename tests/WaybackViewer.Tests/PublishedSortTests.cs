using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class PublishedSortTests
{
    private const string Prefix = "a.com/";
    private const string Filter = "html200";

    private static string Post(string published) =>
        "<html><head><script type=\"application/ld+json\">{\"@type\":\"DiscussionForumPosting\",\"commentCount\":1,\"datePublished\":\"" + published + "\"}</script></head><body>x</body></html>";

    private static async Task<(SqliteArchiveStore Store, PageService Service, List<Capture> Captures)> Seed(string?[] published)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        var caps = SampleData.Captures(published.Length);   // later index = later capture
        await store.SaveSearchAsync(Prefix, Filter, caps, DateTimeOffset.UtcNow);

        // Saved the way an older build did: HTML is stored, but the publish date was never extracted.
        var pages = new List<NewPage>();
        for (var i = 0; i < caps.Count; i++)
        {
            var html = published[i] is null ? "<html><body>x</body></html>" : Post(published[i]!);
            pages.Add(new NewPage(caps[i], HtmlCodec.Compress(html), new ParsedPage(null, null, "x"), DateTimeOffset.UtcNow));
        }
        await store.SavePagesAsync(pages);
        var service = new PageService(store, new FakePageLoader(), null!);
        return (store, service, caps);
    }

    [Fact]
    public async Task Backfill_reads_publish_dates_from_saved_html_and_is_repeatable()
    {
        var (store, service, _) = await Seed(["2023-01-01T00:00:00Z", null, "2024-06-01T00:00:00Z"]);

        Assert.Equal(3, (await store.GetPagesMissingPublishedAsync(Prefix, Filter, 0, 10)).Count);
        Assert.Equal(2, await service.BackfillPublishedAsync(Prefix, Filter));
        // Only the page with no date at all stays pending; a second run changes nothing.
        Assert.Single(await store.GetPagesMissingPublishedAsync(Prefix, Filter, 0, 10));
        Assert.Equal(0, await service.BackfillPublishedAsync(Prefix, Filter));
    }

    [Fact]
    public async Task Published_sort_orders_by_post_date_not_capture_date_and_puts_undated_last()
    {
        // Captures 0,1,2,3 in capture order; post dates: newest post was captured FIRST.
        var (store, service, caps) = await Seed(["2025-05-01T00:00:00Z", "2022-01-01T00:00:00Z", null, "2024-01-01T00:00:00Z"]);
        await service.BackfillPublishedAsync(Prefix, Filter);

        var page = await store.GetPopularPageAsync(Prefix, Filter, new CaptureFilter(), 1, 50, null,
            new PopularityQuery("published", "none", 0));

        Assert.NotNull(page);
        Assert.Equal(
            [caps[0].Timestamp, caps[3].Timestamp, caps[1].Timestamp, caps[2].Timestamp],
            page!.Captures.Select(c => c.Capture.Timestamp).ToArray());
    }

    [Fact]
    public void Time_and_created_timestamp_attributes_give_a_published_date_without_meta_or_json_ld()
    {
        var reddit = PageParser.ExtractMetadata("<html><body><shreddit-post created-timestamp=\"2026-10-01T12:00:00.000Z\" comment-count=\"3\"></shreddit-post></body></html>");
        var generic = PageParser.ExtractMetadata("<html><body><article><time datetime=\"2024-05-06T07:08:09Z\">May 6</time></article></body></html>");
        Assert.Equal(2026, reddit?.PublishedAt?.Year);
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero), generic?.PublishedAt);
    }
}
