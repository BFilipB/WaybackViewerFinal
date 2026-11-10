using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

public class PopularityEstimatorTests
{
    private static PageMetadata Run(string html)
    {
        var parsed = PageParser.Parse(html);
        return PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
    }

    [Fact]
    public void Counts_shown_in_the_page_text_are_read_on_any_site()
    {
        var m = Run("<body><p>Posted</p><span>1,234 views</span><span>56 comments</span></body>");
        Assert.Equal(1234, m.Views);
        Assert.Equal(56, m.Comments);
    }

    [Fact]
    public void Abbreviated_counts_are_read()
    {
        var m = Run("<body><div>2.5k views</div></body>");
        Assert.Equal(2500, m.Views);
    }

    [Fact]
    public void Page_without_counts_gets_nonzero_estimate()
    {
        var m = Run("<body><p>" + new string('x', 4000) + "</p><a href='/a'>a</a></body>");
        Assert.True(m.EffectiveEngagementScore > 0);
    }

    [Fact]
    public void Meta_counts_win_over_text_counts()
    {
        Assert.Equal(900, Run("<meta name='views' content='900'><body>5 views</body>").Views);
    }

    [Fact]
    public void Richer_page_scores_higher_than_bare_page()
    {
        var rich = Run("<body>" + string.Concat(Enumerable.Repeat("<p>text text text text</p><a href='/x'>x</a><img src='i.png'>", 60)) + "</body>");
        Assert.True(rich.EffectiveEngagementScore > Run("<body>hi</body>").EffectiveEngagementScore);
    }

    [Fact]
    public void Garbage_input_never_throws()
    {
        Assert.True(Run("").EffectiveEngagementScore >= 0);
        Assert.True(Run("<<<>>> 99999999999999999999 views").EffectiveEngagementScore >= 0);
    }

    [Fact]
    public void Explicit_zero_is_measured_and_outranks_an_estimate()
    {
        var zero = Run("<body><shreddit-post comment-count=\"0\">post</shreddit-post></body>");
        var estimated = Run("<body><p>" + new string('x', 4000) + "</p><a href='/a'>a</a></body>");
        Assert.Equal(0, zero.Comments);
        Assert.Equal(2, zero.Tier);
        Assert.Equal(1, estimated.Tier);
        Assert.True(zero.Tier > estimated.Tier);
    }

    [Fact]
    public void Archive_signal_only_rows_are_the_lowest_tier()
    {
        Assert.Equal(0, new PageMetadata(EngagementScore: 50, Confidence: 0.1).Tier);
    }

    [Fact]
    public void Structured_score_attribute_is_measured()
    {
        var html = "<shreddit-post score=\"42\" comment-count=\"7\">x</shreddit-post>";
        var m = PopularityEstimator.Enrich(null, html, "x");
        Assert.Equal(42, m.Score);
        Assert.Equal(7, m.Comments);
        Assert.Equal(2, m.Tier);
    }

    [Fact]
    public void Norwegian_forum_counts_are_measured()
    {
        var text = "Tema A 12 svar 340 visninger Tema B 4 svar 90 visninger";
        var m = PopularityEstimator.Enrich(null, "<table></table>", text);
        Assert.Equal(4, m.Comments);
        Assert.Equal(90, m.Views);
        Assert.Equal(2, m.Tier);
    }
}