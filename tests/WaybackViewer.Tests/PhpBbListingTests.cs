using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

/// S5: phpBB listing rows carry replies and views in unlabeled cells (real kvinneguiden.no markup, trimmed).
public class PhpBbListingTests
{
    private static string Row(int replies, int views) =>
        "<tr><td class=\"row2\"><span class=\"topictitle\"><a href=\"viewtopic.php?t=1\" class=\"topictitle\">T</a></span></td>"
        + "<td><span class=\"name\"><a href=\"p\">u</a></span></td>"
        + "<td><span class=\"postdetails\">" + replies + "</span></td>"
        + "<td><span class=\"postdetails\">" + views + "</span></td></tr>";

    [Fact]
    public void Unlabeled_listing_cells_yield_median_replies_and_views_and_keep_zero()
    {
        var html = "<body><table>" + Row(0, 2) + Row(0, 4) + Row(0, 3) + "</table></body>";
        var parsed = PageParser.Parse(html);
        var m = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
        Assert.Equal(0, m.Comments);
        Assert.Equal(3, m.Views);
        Assert.True(m.Tier >= 2);
    }

    [Fact]
    public void Page_without_listing_rows_stays_unknown()
    {
        var html = "<body><p>hello</p></body>";
        var parsed = PageParser.Parse(html);
        var m = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
        Assert.Null(m.Comments);
        Assert.Null(m.Views);
    }
}
