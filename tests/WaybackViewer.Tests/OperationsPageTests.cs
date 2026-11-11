using System.Text.RegularExpressions;
using WaybackViewer.Models;

namespace WaybackViewer.Tests;

/// Server-rendered Operations page: search/sort query parsing, links that keep state, and the bulk-scope indicator.
public class OperationsPageTests
{
    private static async Task<string> GetAsync(AppHost host, string query) =>
        await host.Client.GetStringAsync("operations" + query);

    private static async Task SeedAsync(AppHost host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.Store!.CreateOrGetOperationAsync($"site{i:00}.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Pager_links_keep_search_and_sort_and_clear_drops_only_the_search()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 12);

        var html = await GetAsync(host, "?q=SITE&sort=url&dir=asc&pageSize=10");

        Assert.Contains("site00.com/", html);                                   // url ascending, case-insensitive search
        Assert.DoesNotContain("site11.com/", html);                             // page 2
        Assert.Contains("q=SITE&amp;sort=url&amp;dir=asc", html);               // Next link keeps search + sort
        var clear = Regex.Match(html, "<a href=\"([^\"]*)\">Clear</a>");
        Assert.True(clear.Success);
        Assert.DoesNotContain("q=", clear.Groups[1].Value);
        Assert.Contains("sort=url", clear.Groups[1].Value);
    }

    [Fact]
    public async Task Invalid_sort_falls_back_and_long_search_is_capped()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 1);
        var longText = new string('a', 300);

        var html = await GetAsync(host, $"?sort=bogus&dir=sideways&q={longText}");

        Assert.Matches("value=\"updated\" selected=\"selected\"", html);
        Assert.Matches("value=\"desc\" selected=\"selected\"", html);
        Assert.Contains($"value=\"{new string('a', OperationsControllerLimits.MaxSearch)}\"", html);
        Assert.DoesNotContain(new string('a', OperationsControllerLimits.MaxSearch + 1), html);
    }

    [Fact]
    public async Task Bulk_scope_indicator_warns_when_filters_hide_urls()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 3);

        var plain = await GetAsync(host, "");
        Assert.Contains("All 3 URLs", plain);
        Assert.DoesNotContain("ignores your search/filters", plain);

        var filtered = await GetAsync(host, "?q=site01");
        Assert.Contains("All 3 URLs", filtered);                               // bulk actions still hit every URL
        Assert.Contains("ignores your search/filters", filtered);
        Assert.Contains("not just the 1 shown", filtered);
    }

    private static class OperationsControllerLimits
    {
        public const int MaxSearch = WaybackViewer.Controllers.OperationsController.MaxSearchLength;
    }
}
