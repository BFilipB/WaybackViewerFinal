using Microsoft.Playwright;
using WaybackViewer.Models;

namespace WaybackViewer.Tests;

/// Real-browser layout checks for the Operations table (skipped when Chromium is not installed).
public class OperationsLayoutBrowserTests
{
    [BrowserTheory]
    [InlineData(1920)]
    [InlineData(1400)]
    [InlineData(1100)]
    [InlineData(800)]
    public async Task Operations_table_needs_no_horizontal_scroll_and_keeps_actions_on_one_line(int width)
    {
        await using var host = await AppHost.StartAsync();
        await host.Store!.SaveSearchAsync("example.com/", "html200",
            new[] { new Capture("20200101010101", "https://example.com/" + new string('a', 150), "text/html", "200") },
            DateTimeOffset.UtcNow);
        await host.Store.CreateOrGetOperationAsync("example.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 900 } });
        await page.GotoAsync(new Uri(host.Client.BaseAddress!, "operations").ToString());
        await page.Locator("#opsTable").WaitForAsync();

        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 1"),
            $"page scrolls horizontally at {width}px");
        Assert.True(await page.EvaluateAsync<bool>(
            "(() => { const w = document.querySelector('.table-scroll'); return w.scrollWidth <= w.clientWidth + 1; })()"),
            $"table scrolls horizontally at {width}px: " + await page.EvaluateAsync<string>("(() => { const w = document.querySelector('.table-scroll'); return w.scrollWidth + '/' + w.clientWidth; })()"));

        var tops = await page.EvaluateAsync<int[]>(
            "[...document.querySelectorAll('#opsTable .ops-actions button')].map(b => Math.round(b.getBoundingClientRect().top))");
        Assert.True(tops.Length >= 5);
        Assert.Single(tops.Distinct());
    }
}

/// A [Theory] that is skipped (not failed) when the Playwright Chromium browser is not installed.
public sealed class BrowserTheoryAttribute : TheoryAttribute
{
    public BrowserTheoryAttribute()
    {
        if (!BrowserEnvironment.IsAvailable)
            Skip = "Chromium for Playwright is not installed (see README: Browser tests).";
    }
}
