using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

public class ArchiveSignalsTests
{
    private static StoredCapture Cap(long id, string ts, string url) => new(id, new Capture(ts, url, "text/html", "200"), false);
    private static readonly IReadOnlyDictionary<string, PageMetadata> None = new Dictionary<string, PageMetadata>();

    [Fact]
    public void Every_capture_gets_a_score_so_a_minimum_never_hides_everything()
    {
        var caps = new[] { Cap(1, "20200101000000", "https://a.com/x"), Cap(2, "20200102000000", "https://b.org/y") };
        var m = ArchiveSignals.Fill(caps, None);
        Assert.All(caps, c => Assert.True(m[c.Capture.Key].EffectiveEngagementScore > 0));
    }

    [Fact]
    public void A_url_captured_often_outranks_one_captured_once()
    {
        var caps = Enumerable.Range(1, 6).Select(i => Cap(i, $"2020010{i}000000", "https://s.com/busy"))
            .Append(Cap(9, "20200109000000", "https://s.com/quiet")).ToArray();
        var m = ArchiveSignals.Fill(caps, None);
        Assert.True(m[caps[0].Capture.Key].EffectiveEngagementScore > m[caps[^1].Capture.Key].EffectiveEngagementScore);
    }

    [Fact]
    public void A_section_page_with_many_children_outranks_a_leaf()
    {
        var caps = new List<StoredCapture> { Cap(1, "20200101000000", "https://s.com/forum"), Cap(2, "20200101000000", "https://s.com/leaf") };
        for (var i = 0; i < 8; i++) caps.Add(Cap(10 + i, "20200101000000", $"https://s.com/forum/thread{i}"));
        var m = ArchiveSignals.Fill(caps, None);
        Assert.True(m[caps[0].Capture.Key].EffectiveEngagementScore > m[caps[1].Capture.Key].EffectiveEngagementScore);
    }

    [Fact]
    public void Real_page_data_is_never_overwritten_and_score_is_bounded()
    {
        var c = Cap(1, "20200101000000", "https://s.com/p");
        var real = new PageMetadata(Views: 5000, Confidence: 0.6);
        var m = ArchiveSignals.Fill([c], new Dictionary<string, PageMetadata> { [c.Capture.Key] = real });
        Assert.Equal(5000, m[c.Capture.Key].Views);
        var many = Enumerable.Range(0, 500).Select(i => Cap(i, $"20200101{i:D6}", "https://s.com/p")).ToArray();
        Assert.True(ArchiveSignals.Fill(many, None)[many[0].Capture.Key].EffectiveEngagementScore <= 100);
    }

    [Fact]
    public void Ranking_is_per_site_so_a_quiet_site_still_has_top_pages()
    {
        var caps = new List<StoredCapture>();
        for (var i = 0; i < 6; i++) caps.Add(Cap(i, $"2020010{i}000000", "https://busy.com/hot"));
        caps.Add(Cap(20, "20200101000000", "https://quiet.org/only"));
        caps.Add(Cap(21, "20200101000000", "https://quiet.org/a"));
        caps.Add(Cap(22, "20200101000000", "https://quiet.org/a"));
        var m = ArchiveSignals.Fill(caps, None);
        Assert.True(m[caps[7].Capture.Key].EffectiveEngagementScore > m[caps[6].Capture.Key].EffectiveEngagementScore);
        Assert.True(m[caps[7].Capture.Key].EffectiveEngagementScore >= 50);
    }

    [Fact]
    public void Odd_urls_never_throw()
    {
        var caps = new[] { Cap(1, "1", ""), Cap(2, "2", "/"), Cap(3, "3", "http://") };
        Assert.Equal(3, ArchiveSignals.Fill(caps, None).Count);
    }
}
