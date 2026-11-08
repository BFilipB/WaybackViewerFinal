namespace WaybackViewer.Tests;

public class ThemeAxesTests
{
    [Fact]
    public async Task Layout_offers_clean_light_look_and_motion_axis_without_flash()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.Client.GetStringAsync("/about");
        Assert.Contains("value=\"clean-light\"", html);
        Assert.Contains("id=\"motionSelect\"", html);
        Assert.Contains("value=\"subtle\"", html);
        Assert.Contains("value=\"none\"", html);
        Assert.Contains("prefers-color-scheme: light", html);
        Assert.Contains("prefers-reduced-motion: reduce", html);
        // the inline head script sets theme/layout/motion before first paint
        var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("data-motion", head);
        Assert.Contains("wb-theme", head);
    }

    [Fact]
    public async Task Themes_do_not_touch_the_archived_page_frame_sandbox()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.Client.GetStringAsync("/about");
        Assert.DoesNotContain("allow-same-origin", html);
    }
}
