using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// The rendered table (page 1) and the CaptureSet export must agree on which captures a popularity band keeps and in what order.
public class OrderingParityTests
{
    private static async Task<AppHost> HostAsync()
    {
        var host = await AppHost.StartAsync();
        var caps = new[]
        {
            new Capture("20200101010101", "https://example.com/a", "text/html", "200"),
            new Capture("20200102010101", "https://example.com/b", "text/html", "200"),
            new Capture("20200103010101", "https://example.com/c", "text/html", "200"),
            new Capture("20200104010101", "https://example.com/d", "text/html", "200")
        };
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        long[] likes = [30, 10, 20, 25];
        var pages = new List<NewPage>();
        for (var i = 0; i < 4; i++)
            pages.Add(new NewPage(caps[i], HtmlCodec.Compress("<html><body>p" + i + "</body></html>"),
                new ParsedPage("p" + i, null, "p" + i, new PageMetadata(Likes: likes[i], Confidence: 0.6)), DateTimeOffset.UtcNow));
        await host.Store.SavePagesAsync(pages);
        return host;
    }

    private static string[] Order(string body) =>
        new[] { "20200101010101", "20200102010101", "20200103010101", "20200104010101" }
            .Select(ts => (ts, i: body.IndexOf(ts, StringComparison.Ordinal))).Where(x => x.i >= 0)
            .OrderBy(x => x.i).Select(x => x.ts).ToArray();

    [Theory]
    [InlineData("sort=liked", new[] { "20200101010101", "20200104010101", "20200103010101", "20200102010101" })]
    [InlineData("sort=liked&popularityMetric=likes&minPopularity=15&maxPopularity=28", new[] { "20200104010101", "20200103010101" })]
    [InlineData("sort=least-liked&popularityMetric=likes&minPopularity=10", new[] { "20200102010101", "20200103010101", "20200104010101", "20200101010101" })]
    public async Task Table_and_export_return_the_same_captures_in_the_same_order(string query, string[] expected)
    {
        await using var host = await HostAsync();
        var table = await host.GetAsync("/?url=example.com/&" + query);
        var export = await host.GetAsync("/Home/CaptureSet?url=example.com/&" + query);
        Assert.Equal(expected, Order(table).Where(expected.Contains).ToArray());
        Assert.Equal(expected, Order(export).Where(expected.Contains).ToArray());
        foreach (var ts in new[] { "20200101010101", "20200102010101", "20200103010101", "20200104010101" }.Except(expected))
        {
            Assert.DoesNotContain("data-ts=\"" + ts + "\"", table);
            Assert.DoesNotContain(ts, export);
        }
    }
}