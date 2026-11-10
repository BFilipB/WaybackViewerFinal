using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class HomeCatalogWaitTests
{
    [Fact]
    public async Task Slow_cdx_download_defers_to_a_background_load_instead_of_holding_the_page()
    {
        await using var host = await AppHost.StartAsync(new Dictionary<string, string?> { ["Performance:CatalogWaitMs"] = "100" });
        host.RouteSite("example.com/", SampleData.Captures(3));
        host.Archive.BeforeResponse = _ => Task.Delay(1500);

        var started = DateTime.UtcNow;
        var html = await host.GetAsync("/?url=example.com/");

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.2), "page must not wait for the whole download");
        Assert.Contains("still loading", html);
    }
}
