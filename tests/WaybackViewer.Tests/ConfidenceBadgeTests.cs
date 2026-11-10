using WaybackViewer.Models;

namespace WaybackViewer.Tests;

/// Row confidence (High/Med/Low) follows the measured / estimated / archive-signal tiers.
public class ConfidenceBadgeTests
{
    [Theory]
    [InlineData(0.6, 2)]
    [InlineData(0.2, 1)]
    [InlineData(0.1, 0)]
    public void Tier_maps_confidence(double confidence, int tier) =>
        Assert.Equal(tier, new PageMetadata(Confidence: confidence).Tier);

    [Fact]
    public void Engagement_is_log_scaled_so_views_do_not_drown_comments()
    {
        Assert.True(PageMetadata.Engagement(null, null, 50, null, null) > PageMetadata.Engagement(500, null, null, null, null));
        Assert.Equal(0, PageMetadata.Engagement(0, 0, 0, 0, 0));
    }
}
