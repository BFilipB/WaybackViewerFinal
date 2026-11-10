using WaybackViewer.Models;
using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

/// Re-created extraction quality tests (see docs/popularity/scoring.md); each pins one failure mode.
public class ExtractionQualityTests
{
    private static PageMetadata Run(string html)
    {
        var parsed = PageParser.Parse(html);
        return PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
    }

    [Fact]
    public void Json_ld_post_counts_win_and_nested_replies_are_ignored()
    {
        var html = "<html><head><script type=\"application/ld+json\">[{\"@type\":\"BreadcrumbList\"},{\"@type\":\"DiscussionForumPosting\",\"commentCount\":13,"
            + "\"interactionStatistic\":{\"@type\":\"InteractionCounter\",\"interactionType\":{\"@type\":\"LikeAction\"},\"userInteractionCount\":10},"
            + "\"comment\":[{\"@type\":\"Comment\",\"upvoteCount\":8,\"commentCount\":3}]}]</script></head><body><p>5 comments</p></body></html>";
        var m = Run(html);
        Assert.Equal(13, m.Comments);
        Assert.Equal(10, m.Likes);
    }

    [Fact]
    public void Json_ld_date_published_is_read_for_the_published_sort()
    {
        var html = "<html><head><script type=\"application/ld+json\">{\"@type\":\"DiscussionForumPosting\",\"commentCount\":1,\"datePublished\":\"2025-05-08T09:28:14.326Z\"}</script></head><body></body></html>";
        Assert.Equal(new DateTimeOffset(2025, 5, 8, 9, 28, 14, 326, TimeSpan.Zero), Run(html).PublishedAt);
    }

    [Fact]
    public void Structured_comment_count_attribute_beats_text_counts()
    {
        Assert.Equal(13, Run("<body><shreddit-post comment-count=\"13\"></shreddit-post><p>2 comments</p></body>").Comments);
    }

    [Fact]
    public void Explicit_zero_structured_comment_count_is_kept()
    {
        var m = Run("<body><shreddit-post comment-count=\"0\"></shreddit-post></body>");
        Assert.Equal(0, m.Comments);
        Assert.True(m.Tier >= 2);
    }

    [Fact]
    public void Gold_forum_post_with_more_comments_ranks_above_post_with_fewer()
    {
        var more = Run("<body><shreddit-post comment-count=\"40\"></shreddit-post></body>");
        var fewer = Run("<body><shreddit-post comment-count=\"3\"></shreddit-post></body>");
        Assert.True(more.EngagementScore > fewer.EngagementScore);
    }

    [Fact]
    public void Posts_own_comment_count_wins_over_counts_of_related_posts()
    {
        Assert.Equal(13, Run("<body><shreddit-post comment-count=\"13\"></shreddit-post><a>9 comments</a><a>7 comments</a></body>").Comments);
    }

    [Fact]
    public void Sidebar_before_post_does_not_decide_the_comment_count()
    {
        Assert.Equal(30, Run("<body><aside>1 comments</aside><main>30 comments</main><footer>32 comments</footer></body>").Comments);
    }
}
