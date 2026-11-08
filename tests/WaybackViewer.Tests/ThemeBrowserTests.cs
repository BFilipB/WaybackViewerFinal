using Microsoft.Playwright;

namespace WaybackViewer.Tests;

public class ThemeBrowserTests
{
    [BrowserFact]
    public async Task Every_look_and_layout_renders_one_page_of_rows_without_script_errors()
    {
        await using var host = await AppHost.StartAsync();
        var caps = Enumerable.Range(1, 25).Select(i => new WaybackViewer.Models.Capture($"202001{i:00}010101", $"https://example.com/p{i}", "text/html", "200")).ToArray();
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync(host.Client.BaseAddress + "?url=example.com%2F&filter=html200&pagination=server&pageSize=10");
        var themes = await page.Locator("#themeSelect option").EvaluateAllAsync<string[]>("o => o.map(x => x.value)");
        var layouts = await page.Locator("#layoutSelect option").EvaluateAllAsync<string[]>("o => o.map(x => x.value)");
        var motions = await page.Locator("#motionSelect option").EvaluateAllAsync<string[]>("o => o.map(x => x.value)");
        Assert.Contains("clean-light", themes);
        Assert.Contains("cards", layouts);
        var baseline = await page.Locator(".capture-row").CountAsync();
        Assert.Equal(10, baseline);   // one page of 25 captures
        foreach (var t in themes)
            foreach (var l in layouts)
            {
                await page.SelectOptionAsync("#themeSelect", t);
                await page.SelectOptionAsync("#layoutSelect", l);
                Assert.Equal(t, await page.EvaluateAsync<string>("document.documentElement.dataset.theme"));
                Assert.Equal(l, await page.EvaluateAsync<string>("document.documentElement.dataset.layout"));
                Assert.Equal(baseline, await page.Locator(".capture-row").CountAsync());   // pagination unaffected
            }
        foreach (var m in motions)
        {
            await page.SelectOptionAsync("#motionSelect", m);
            Assert.Equal(m, await page.EvaluateAsync<string>("document.documentElement.dataset.motion"));
        }
        Assert.Empty(errors);
    }
}
