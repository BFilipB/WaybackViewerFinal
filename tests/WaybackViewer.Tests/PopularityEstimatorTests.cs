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
}
