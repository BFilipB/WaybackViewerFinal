using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

/// Reddit markup eras (trimmed synthetic fixtures): new shreddit elements, old.reddit data attributes.
public class RedditAttributeTests
{
    private static Models.PageMetadata Run(string html)
    {
        var parsed = PageParser.Parse(html);
        return PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
    }

    [Fact]
    public void Shreddit_post_attributes_give_score_and_comments()
    {
        var m = Run("<body><shreddit-post score=\"42\" comment-count=\"13\"></shreddit-post></body>");
        Assert.Equal(42, m.Score);
        Assert.Equal(13, m.Comments);
        Assert.True(m.Tier >= 2);
    }

    [Fact]
    public void Old_reddit_data_attributes_give_score_and_comments()
    {
        var m = Run("<body><div class=\"thing\" data-score=\"7\" data-comments-count=\"3\"></div></body>");
        Assert.Equal(7, m.Score);
        Assert.Equal(3, m.Comments);
    }

    [Fact]
    public void Explicit_zero_score_is_kept_and_stays_measured()
    {
        var m = Run("<body><shreddit-post score=\"0\" comment-count=\"0\"></shreddit-post></body>");
        Assert.Equal(0, m.Score);
        Assert.Equal(0, m.Comments);
        Assert.True(m.Tier >= 2);
    }

    [Fact]
    public void Page_without_reddit_attributes_has_unknown_score()
    {
        var m = Run("<body><p>hello</p></body>");
        Assert.Null(m.Score);
    }
}