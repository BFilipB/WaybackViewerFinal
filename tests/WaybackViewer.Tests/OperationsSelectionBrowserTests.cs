using Microsoft.Playwright;
using WaybackViewer.Models;

namespace WaybackViewer.Tests;

/// Real-browser checks of the Operations bulk selection (skipped when Chromium is not installed).
public class OperationsSelectionBrowserTests
{
    private static async Task<(AppHost Host, List<long> Keep, long Other)> SeedAsync()
    {
        var host = await AppHost.StartAsync();
        var keep = new List<long>();
        for (var i = 0; i < 25; i++)
            keep.Add((await host.Store!.CreateOrGetOperationAsync($"keep{i:00}.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow)).Id);
        var other = await host.Store!.CreateOrGetOperationAsync("other.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
        return (host, keep, other.Id);
    }

    private static async Task<(IPlaywright Pw, IBrowser Browser, IPage Page)> OpenAsync(AppHost host, string query)
    {
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(new Uri(host.Client.BaseAddress!, "operations" + query).ToString());
        await page.Locator("#opsTable").WaitForAsync();
        return (pw, browser, page);
    }

    private static Task<bool[]> ChecksAsync(IPage page) =>
        page.EvaluateAsync<bool[]>("[...document.querySelectorAll('#opsTable .ops-pick')].map(c => c.checked)");

    [BrowserFact]
    public async Task Select_all_matching_stays_selected_on_other_pages_and_acts_only_on_the_search()
    {
        var (host, keep, other) = await SeedAsync();
        await using var _ = host;
        var (pw, browser, page) = await OpenAsync(host, "?q=keep&pageSize=10&sort=url&dir=asc");
        using var __ = pw; await using var ___ = browser;

        Assert.Equal("off", await page.GetAttributeAsync("input[name=q]", "autocomplete"));
        await page.CheckAsync("#opsAll");
        Assert.Equal("Select all 25 matching URLs", (await page.InnerTextAsync("#opsSelectMatching")).Trim());
        await page.ClickAsync("#opsSelectMatching");

        await page.ClickAsync(".pager a:has-text('Next')");
        await page.Locator("#opsTable").WaitForAsync();
        Assert.All(await ChecksAsync(page), Assert.True);
        Assert.StartsWith("25 selected", await page.InnerTextAsync("#opsSelected"));

        await page.ClickAsync("[data-bulk=pause]");
        await page.Locator("#opsMessage", new() { HasTextString = "Applied to 25" }).WaitForAsync();
        foreach (var id in keep) Assert.Equal(OperationState.Paused, (await host.Store!.GetOperationAsync(id))!.State);
        Assert.Equal(OperationState.Queued, (await host.Store!.GetOperationAsync(other))!.State);
    }

    [BrowserFact]
    public async Task Individual_ticks_add_up_across_pages_and_clear_resets_every_page()
    {
        var (host, _, _) = await SeedAsync();
        await using var h = host;
        var (pw, browser, page) = await OpenAsync(host, "?q=keep&pageSize=10&sort=url&dir=asc");
        using var __ = pw; await using var ___ = browser;

        await page.Locator("#opsTable .ops-pick").First.CheckAsync();
        await page.ClickAsync(".pager a:has-text('Next')");
        await page.Locator("#opsTable").WaitForAsync();
        await page.Locator("#opsTable .ops-pick").First.CheckAsync();
        Assert.Equal("2 selected (across pages)", (await page.InnerTextAsync("#opsSelected")).Trim());

        await page.ClickAsync(".pager a:has-text('Prev')");
        await page.Locator("#opsTable").WaitForAsync();
        Assert.True((await ChecksAsync(page))[0]);

        await page.ClickAsync("#opsClearSelection");
        await page.ClickAsync(".pager a:has-text('Next')");
        await page.Locator("#opsTable").WaitForAsync();
        Assert.All(await ChecksAsync(page), Assert.False);
        Assert.StartsWith("0 selected", await page.InnerTextAsync("#opsSelected"));
    }
}
