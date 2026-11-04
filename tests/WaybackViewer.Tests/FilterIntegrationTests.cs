using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Every filter, sort and paging control must survive the use of every other one.
public class FilterIntegrationTests
{
    private static async Task<AppHost> HostAsync()
    {
        var host = await AppHost.StartAsync();
        var caps = Enumerable.Range(1, 6).Select(i =>
            new Capture($"2020010{i}010101", $"https://example.com/{(i % 2 == 0 ? "blog" : "docs")}/p{i}", "text/html", "200")).ToArray();
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        var pages = caps.Select((c, i) => new NewPage(c, HtmlCodec.Compress($"<html><body>zebra{i}</body></html>"),
            new ParsedPage("p" + i, null, "zebra" + i, new PageMetadata(Likes: 10 * (i + 1))), DateTimeOffset.UtcNow)).ToList();
        await host.Store.SavePagesAsync(pages);
        return host;
    }

    [Fact]
    public async Task Pager_and_facet_links_keep_sort_popularity_download_scope_and_facets()
    {
        await using var host = await HostAsync();
        var html = await host.GetAsync("/?url=example.com/&sort=liked&popularityMetric=likes&minPopularity=10&section=%2Fblog&pageSize=25&downloadAll=true");
        Assert.Contains("sort=liked", html);
        Assert.Contains("popularityMetric=likes", html);
        Assert.Contains("section=%2Fblog", html);
        Assert.Contains("downloadAll=true", html);
        Assert.DoesNotContain("data-ts=\"20200101010101\"", html);   // /docs/ capture is filtered out by section
    }

    [Fact]
    public async Task Content_mode_chosen_with_text_prefix_survives_in_generated_links()
    {
        await using var host = await HostAsync();
        var html = await host.GetAsync("/?url=example.com/&q=text:zebra3&sort=liked");
        Assert.Contains("searchMode=content", html);
    }

    [Fact]
    public async Task Invalid_url_keeps_sort_and_popularity_selection()
    {
        await using var host = await HostAsync();
        var html = await host.GetAsync("/?url=not%20valid&sort=liked&popularityMetric=likes&minPopularity=5");
        Assert.Contains("value=\"liked\" selected=\"selected\"", html);
        Assert.Contains("value=\"likes\" selected=\"selected\"", html);
    }

    [Fact]
    public async Task Facet_content_popularity_and_sort_combine_and_export_matches_the_view()
    {
        await using var host = await HostAsync();
        const string q = "url=example.com/&searchMode=content&q=zebra&section=%2Fblog&sort=liked&popularityMetric=likes&minPopularity=20";
        var view = await host.GetAsync("/?" + q);
        var export = await host.GetAsync("/Home/CaptureSet?" + q);
        foreach (var ts in new[] { "20200102010101", "20200104010101", "20200106010101" })
        {
            Assert.Contains($"data-ts=\"{ts}\"", view);
            Assert.Contains(ts, export);
        }
        Assert.DoesNotContain("20200101010101", export);
        Assert.DoesNotContain("data-ts=\"20200103010101\"", view);
    }

    [Theory]
    [InlineData("newest", "none", "server", 25, "html200", "url")]
    [InlineData("popular", "views", "client", 50, "html", "content")]
    [InlineData("liked", "likes", "server", 100, "all", "url")]
    [InlineData("score", "score", "server", 200, "html200", "content")]
    [InlineData("engaged", "engaged", "client", 25, "all", "content")]
    [InlineData("viewed", "comments", "server", 50, "html", "url")]
    public async Task Every_control_combination_renders_without_error(string sort, string metric, string pagination, int size, string filter, string mode)
    {
        await using var host = await HostAsync();
        var html = await host.GetAsync($"/?url=example.com/&sort={sort}&popularityMetric={metric}&minPopularity=1&pagination={pagination}&pageSize={size}&filter={filter}&searchMode={mode}&q=zebra&year=2020&category=Pages&status=200&type=page&unique=true");
        Assert.DoesNotContain("Exception", html);
        Assert.Contains("id=\"searchBar\"", html);
    }
}
