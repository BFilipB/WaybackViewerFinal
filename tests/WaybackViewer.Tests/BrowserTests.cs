using Microsoft.Playwright;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// A [Fact] that is skipped (not failed) when the Playwright Chromium browser is not installed.
/// One-time setup:  pwsh tests/WaybackViewer.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (!BrowserEnvironment.IsAvailable)
            Skip = "Chromium for Playwright is not installed (see README: Browser tests).";
    }
}

public static class BrowserEnvironment
{
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        try
        {
            using var pw = Playwright.CreateAsync().GetAwaiter().GetResult();
            return File.Exists(pw.Chromium.ExecutablePath);
        }
        catch { return false; }
    }
}

/// Drives the real UI in a real browser against the real app (fake archive, in-memory store).
public class BrowserTests
{
    private static async Task<(AppHost host, IPlaywright pw, IBrowser browser)> StartAsync()
    {
        var host = await AppHost.StartAsync();
        var caps = new[]
        {
            new Capture("20200101010101", "https://example.com/a", "text/html", "200"),
            new Capture("20200102010101", "https://example.com/b", "text/html", "200"),
            new Capture("20200103010101", "https://example.com/c", "text/html", "200")
        };
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        return (host, pw, browser);
    }

    [BrowserFact]
    public async Task Theme_and_layout_choices_survive_a_reload()
    {
        var (host, pw, browser) = await StartAsync();
        await using var _h = host; using var _p = pw; await using var _b = browser;
        var page = await browser.NewPageAsync();
        await page.GotoAsync(host.Client.BaseAddress!.ToString());

        var themes = await page.Locator("#themeSelect option").EvaluateAllAsync<string[]>("o => o.map(x => x.value)");
        var layouts = await page.Locator("#layoutSelect option").EvaluateAllAsync<string[]>("o => o.map(x => x.value)");
        var theme = themes.Last();
        var layout = layouts.Last();
        await page.SelectOptionAsync("#themeSelect", theme);
        await page.SelectOptionAsync("#layoutSelect", layout);
        Assert.Equal(theme, await page.EvaluateAsync<string>("document.documentElement.dataset.theme"));
        Assert.Equal(layout, await page.EvaluateAsync<string>("document.documentElement.dataset.layout"));

        await page.ReloadAsync();
        Assert.Equal(theme, await page.EvaluateAsync<string>("document.documentElement.dataset.theme"));
        Assert.Equal(layout, await page.EvaluateAsync<string>("document.documentElement.dataset.layout"));
        Assert.Equal(theme, await page.InputValueAsync("#themeSelect"));
    }

    [BrowserFact]
    public async Task Alt_T_and_Alt_L_cycle_theme_and_layout()
    {
        var (host, pw, browser) = await StartAsync();
        await using var _h = host; using var _p = pw; await using var _b = browser;
        var page = await browser.NewPageAsync();
        await page.GotoAsync(host.Client.BaseAddress!.ToString());

        var before = await page.EvaluateAsync<string>("document.documentElement.dataset.theme || ''");
        await page.Keyboard.PressAsync("Alt+T");
        var after = await page.EvaluateAsync<string>("document.documentElement.dataset.theme || ''");
        Assert.NotEqual(before, after);

        var lBefore = await page.EvaluateAsync<string>("document.documentElement.dataset.layout || ''");
        await page.Keyboard.PressAsync("Alt+L");
        var lAfter = await page.EvaluateAsync<string>("document.documentElement.dataset.layout || ''");
        Assert.NotEqual(lBefore, lAfter);
    }

    [BrowserFact]
    public async Task Saved_views_are_stored_listed_and_restore_the_search()
    {
        var (host, pw, browser) = await StartAsync();
        await using var _h = host; using var _p = pw; await using var _b = browser;
        var page = await browser.NewPageAsync();
        page.Dialog += async (_, d) => await d.AcceptAsync("My view");
        await page.GotoAsync(host.Client.BaseAddress + "?url=example.com/&sort=liked&popularityMetric=likes");

        await page.ClickAsync("#saveView");
        var stored = await page.EvaluateAsync<string>("localStorage.getItem('wb-saved-views') || ''");
        Assert.Contains("My view", stored);
        Assert.Contains("sort=liked", stored);

        await page.ReloadAsync();
        var link = page.Locator("#savedViews a", new() { HasTextString = "My view" });
        Assert.Equal(1, await link.CountAsync());
        Assert.Contains("sort=liked", await link.GetAttributeAsync("href"));
    }

    [BrowserFact]
    public async Task Sort_and_filter_choice_is_remembered_and_page_has_no_script_errors()
    {
        var (host, pw, browser) = await StartAsync();
        await using var _h = host; using var _p = pw; await using var _b = browser;
        var page = await browser.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, e) => errors.Add(e);
        await page.GotoAsync(host.Client.BaseAddress + "?url=example.com/");
        await page.SelectOptionAsync("select[name=sort]", "liked");
        await page.WaitForTimeoutAsync(300);
        Assert.Empty(errors);
        var stored = await page.EvaluateAsync<string>("localStorage.getItem('wb-sort-filter') || ''");
        Assert.Contains("liked", stored);
    }
}
