using Microsoft.Playwright;

namespace WaybackViewer.Tests;

/// Card view: buttons never overlap the URL/details text, at desktop and phone widths, for every layout.
public class CardLayoutBrowserTests
{
    [BrowserFact]
    public async Task Card_buttons_never_overlap_text_and_nothing_overflows_the_card()
    {
        await using var host = await AppHost.StartAsync();
        var longPath = string.Join("/", Enumerable.Repeat("very-long-segment-name", 12));
        var caps = Enumerable.Range(1, 12).Select(i => new WaybackViewer.Models.Capture(
            $"202001{i:00}010101", $"https://example.com/{longPath}/p{i}?query=with-many-characters-{i}", "text/html", "200")).ToArray();
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);

        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });

        foreach (var width in new[] { 1400, 900, 390 })
        {
            var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = 900 } });
            await page.GotoAsync(host.Client.BaseAddress + "?url=example.com%2F&filter=html200&pagination=server&pageSize=10");
            // rows use content-visibility:auto (off-screen rows skip layout until scrolled into view); force layout so every card is measured
            await page.AddStyleTagAsync(new() { Content = ".capture-row { content-visibility: visible !important; }" });
            var layouts = await page.Locator("#layoutSelect option").EvaluateAllAsync<string[]>("o => o.map(x => x.value)");
            foreach (var layout in layouts)
            {
                await page.SelectOptionAsync("#layoutSelect", layout);
                var problems = await page.EvaluateAsync<string[]>(@"() => {
                    const out = [];
                    const hit = (a, b) => a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;
                    document.querySelectorAll('.capture-row').forEach((card, i) => {
                        const c = card.getBoundingClientRect();
                        const texts = [...card.querySelectorAll('.url, .capture-details, .capture-date, .popularity-badges')].map(e => e.getBoundingClientRect()).filter(r => r.width > 0);
                        card.querySelectorAll('.row .btn, .row button').forEach(b => {
                            const r = b.getBoundingClientRect();
                            if (r.width === 0) return;
                            if (texts.some(t => hit(r, t))) out.push('card ' + i + ': button overlaps text');
                            if (r.right > c.right + 1 || r.left < c.left - 1) out.push('card ' + i + ': button outside card');
                            if (b.scrollWidth > b.clientWidth + 1) out.push('card ' + i + ': button label clipped');
                        });
                    });
                    return out;
                }");
                Assert.True(problems.Length == 0, $"width {width}, layout {layout}: {string.Join("; ", problems.Distinct())}");
            }
            await page.CloseAsync();
        }
    }
}
