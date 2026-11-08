using System.Globalization;
using System.Text.RegularExpressions;

namespace WaybackViewer.Tests;

public class ThemeContrastTests
{
    private static double Lum(string hex)
    {
        double C(int i) { var v = int.Parse(hex.Substring(1 + i * 2, 2), NumberStyles.HexNumber) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
    }
    private static double Ratio(string a, string b) { var x = Lum(a); var y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    [Theory]
    [InlineData("clean-light")]
    [InlineData("archive-paper")]
    [InlineData("neon-terminal")]
    [InlineData("high-contrast")]
    [InlineData("midnight-command")]
    public async Task Theme_text_and_badges_meet_contrast(string theme)
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.Client.GetStringAsync("/about");
        var m = Regex.Match(html, @"data-theme='" + theme + @"'\] \{([^}]*)\}");
        Assert.True(m.Success, theme);
        string V(string n) => Regex.Match(m.Groups[1].Value, "--" + n + @":(#[0-9a-fA-F]{6})").Groups[1].Value;
        Assert.True(Ratio(V("text"), V("bg")) >= 4.5, "text/bg");
        Assert.True(Ratio(V("text"), V("panel")) >= 4.5, "text/panel");
        Assert.True(Ratio(V("muted"), V("panel")) >= 4.5, "muted/panel");
        Assert.True(Ratio(V("accent"), V("panel")) >= 3.0, "accent/panel");
        Assert.True(Ratio(V("ok"), V("panel")) >= 3.0, "ok/panel");
        Assert.True(Ratio(V("warn"), V("panel")) >= 3.0, "warn/panel");
    }

    [Fact]
    public async Task All_looks_and_layouts_are_offered()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.Client.GetStringAsync("/about");
        foreach (var v in new[] { "archive-paper", "neon-terminal", "high-contrast", "cards", "split", "timeline" })
            Assert.Contains("value=\"" + v + "\"", html);
        Assert.Contains("--radius:", html);   // design tokens present
    }
}
