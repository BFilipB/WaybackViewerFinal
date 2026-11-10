using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Quality rules for popularity v2 (see docs/popularity/scoring.md). Each test pins one failure mode.
public class PopularityQualityTests
{
    private static PageMetadata Run(string html)
    {
        var parsed = PageParser.Parse(html);
        return PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
    }

    [Fact]
    public void Stray_sidebar_count_does_not_override_the_typical_value()
    {
        // First match (sidebar "1 views") used to win; median of 1, 5000, 5100 is 5000.
        var m = Run("<body><aside>1 views</aside><main>5,000 views</main><footer>5,100 views</footer></body>");
        Assert.Equal(5000, m.Views);
    }

    [Fact]
    public void Comment_markup_in_css_and_scripts_does_not_inflate_the_estimate()
    {
        var plain = "<body><p>" + new string('x', 2000) + "</p></body>";
        var boiler = "<body><div class='comment-form comment-respond comment-reply'></div><script>var comment=1;comment++;</script><p>"
                     + new string('x', 2000) + "</p></body>";
        Assert.Equal(Run(plain).EffectiveEngagementScore, Run(boiler).EffectiveEngagementScore);
    }

    [Fact]
    public void Visible_discussion_still_raises_the_estimate()
    {
        var quiet = Run("<body><p>" + new string('x', 2000) + "</p></body>");
        var talk = Run("<body><p>" + new string('x', 2000) + "</p><p>comment</p><p>reply</p><p>comment</p></body>");
        Assert.True(talk.EffectiveEngagementScore > quiet.EffectiveEngagementScore);
    }

    [Theory]
    [InlineData(12, 50)]
    [InlineData(20, 100)]
    [InlineData(50, 200)]
    public void Archive_signal_keeps_separating_heavily_captured_urls(int fewer, int more)
    {
        // Old formula capped at 12 captures, so all these pairs tied.
        Assert.True(ArchiveSignals.RawSignal(more, 0) > ArchiveSignals.RawSignal(fewer, 0));
    }

    [Fact]
    public void Archive_signal_keeps_separating_big_hubs()
    {
        Assert.True(ArchiveSignals.RawSignal(1, 200) > ArchiveSignals.RawSignal(1, 20));
    }

    [Fact]
    public void Archive_signal_is_bounded_and_monotonic()
    {
        var last = -1;
        for (var n = 0; n < 5000; n += 7)
        {
            var v = ArchiveSignals.RawSignal(n, n);
            Assert.InRange(v, 0, 100);
            Assert.True(v >= last);
            last = v;
        }
        Assert.Equal(0, ArchiveSignals.RawSignal(0, 0));
    }

    [Fact]
    public void Gold_ranking_orders_hub_and_frequently_captured_pages_first()
    {
        // Small gold set: expected order top->bottom on a forum-like site using archive signals only.
        var captures = new List<StoredCapture>();
        long id = 1;
        void Add(string url, int times)
        {
            for (var i = 0; i < times; i++)
                captures.Add(new StoredCapture(id++, new Capture($"2020{(i % 12) + 1:00}01000000", url, "text/html", "200"), false));
        }
        Add("https://forum.example/", 80);
        for (var t = 0; t < 30; t++) Add($"https://forum.example/thread/{t}", t == 0 ? 40 : 1);
        Add("https://forum.example/about", 2);

        var scored = ArchiveSignals.Fill(captures, new Dictionary<string, PageMetadata>());
        long Score(string url) => scored[captures.First(c => c.Capture.Original == url).Capture.Key].EffectiveEngagementScore;

        Assert.True(Score("https://forum.example/") > Score("https://forum.example/thread/0"));
        Assert.True(Score("https://forum.example/thread/0") > Score("https://forum.example/thread/5"));
        Assert.True(Score("https://forum.example/about") >= Score("https://forum.example/thread/5"));
    }

    [Fact]
    public async Task Single_metric_sort_puts_unknown_last_and_least_is_not_the_same_as_most()
    {
        // S7: unknown views were treated as 0, so "least viewed" listed unknown pages first (and tied rows looked identical to "most viewed").
        var dir = Path.Combine(Path.GetTempPath(), "wbv-s7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(8), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;

        NewPage Save(StoredCapture c, PageMetadata m) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"), new ParsedPage(null, null, "x", m), DateTimeOffset.UtcNow);
        var v500 = all[0]; var v50 = all[1]; var v0 = all[2]; var noViews1 = all[3]; var noViews2 = all[4];
        await store.SavePagesAsync([
            Save(v500, new PageMetadata(Views: 500, EngagementScore: 500, Confidence: 0.6)),
            Save(v50, new PageMetadata(Views: 50, EngagementScore: 50, Confidence: 0.6)),
            Save(v0, new PageMetadata(Views: 0, EngagementScore: 0, Confidence: 0.6)),
            Save(noViews1, new PageMetadata(Comments: 9, EngagementScore: 36, Confidence: 0.6)),
            Save(noViews2, new PageMetadata(Likes: 3, EngagementScore: 9, Confidence: 0.6))]);

        var most = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("viewed", "none", 0));
        var least = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("least-viewed", "none", 0));

        Assert.Equal([v500.Id, v50.Id, v0.Id], most!.Captures.Select(c => c.Id).Take(3));
        Assert.Equal([v0.Id, v50.Id, v500.Id], least!.Captures.Select(c => c.Id).Take(3));
    }
    [Fact]
    public async Task Views_sort_ranks_rows_without_views_by_an_estimated_proxy_after_real_views()
    {
        // Reddit-like sites have no view counts: rows with comments/likes form a middle level ordered by engagement, rows with nothing come last.
        var dir = Path.Combine(Path.GetTempPath(), "wbv-s8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteArchiveStore($"Data Source={Path.Combine(dir, "d.db")}", new HtmlFileStore(Path.Combine(dir, "html")));
        await store.InitializeAsync();
        await store.SaveSearchAsync("a.com/", "all", SampleData.Captures(8), DateTimeOffset.UtcNow);
        var all = (await store.GetSearchAsync("a.com/", "all"))!.Captures;

        NewPage Save(StoredCapture c, PageMetadata m) =>
            new(c.Capture, HtmlCodec.Compress("<html><body>x</body></html>"), new ParsedPage(null, null, "x", m), DateTimeOffset.UtcNow);
        var v500 = all[0]; var v50 = all[1]; var c40 = all[2]; var c2 = all[3]; var none = all[4];
        await store.SavePagesAsync([
            Save(v500, new PageMetadata(Views: 500, EngagementScore: 500, Confidence: 0.6)),
            Save(v50, new PageMetadata(Views: 50, EngagementScore: 50, Confidence: 0.6)),
            Save(c40, new PageMetadata(Comments: 40, EngagementScore: 160, Confidence: 0.6)),
            Save(c2, new PageMetadata(Comments: 2, EngagementScore: 8, Confidence: 0.6)),
            Save(none, new PageMetadata(EngagementScore: 120, Confidence: 0.2))]);

        var most = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("viewed", "none", 0));
        var least = await store.GetPopularPageAsync("a.com/", "all", new CaptureFilter(), 1, 100, null, new PopularityQuery("least-viewed", "none", 0));

        Assert.Equal([v500.Id, v50.Id, c40.Id, c2.Id, none.Id], most!.Captures.Select(c => c.Id).Take(5));
        Assert.Equal([v50.Id, v500.Id, c2.Id, c40.Id, none.Id], least!.Captures.Select(c => c.Id).Take(5));
    }}
