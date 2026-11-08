using System.Text.Json;
using Capture = WaybackViewer.Models.Capture;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class ExportAndHistoryHttpTests
{
    private static async Task<AppHost> HostAsync(int count)
    {
        var host = await AppHost.StartAsync();
        var caps = Enumerable.Range(0, count).Select(i =>
            new Capture(new DateTime(2020, 1, 1).AddMinutes(i).ToString("yyyyMMddHHmmss"),
                $"https://example.com/{(i % 3 == 0 ? "blog" : "docs")}/p{i % 30}", "text/html", "200")).ToArray();
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        return host;
    }

    [Fact]
    public async Task Export_streams_every_capture_across_multiple_sql_pages()
    {
        await using var host = await HostAsync(4100);
        var json = await host.GetAsync("/Home/CaptureSet?url=example.com/");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(4100, doc.RootElement.GetArrayLength());
        var ts = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("ts").GetString()).Distinct().Count();
        Assert.Equal(4100, ts);
    }

    [Fact]
    public async Task Export_honours_structural_filters()
    {
        await using var host = await HostAsync(300);
        var json = await host.GetAsync("/Home/CaptureSet?url=example.com/&section=%2Fblog");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(100, doc.RootElement.GetArrayLength());
        Assert.All(doc.RootElement.EnumerateArray(), e => Assert.Contains("/blog/", e.GetProperty("url").GetString()));
    }

    [Fact]
    public async Task History_returns_at_most_sixty_newest_first()
    {
        await using var host = await HostAsync(3000);
        var json = await host.GetAsync("/Home/History?url=example.com/&original=" + Uri.EscapeDataString("https://example.com/blog/p0"));
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("ts").GetString()!).ToList();
        Assert.Equal(60, rows.Count);
        Assert.Equal(rows.OrderByDescending(r => r, StringComparer.Ordinal), rows);
    }
}




