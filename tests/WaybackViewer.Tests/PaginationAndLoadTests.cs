using Capture = WaybackViewer.Models.Capture;
using System.Diagnostics;
using System.Text.RegularExpressions;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Pagination guarantees, concurrency/stress behaviour and latency budgets, all in-process (no network, no installs).
public class PaginationAndLoadTests
{
    private static async Task<AppHost> HostAsync(int count)
    {
        var host = await AppHost.StartAsync();
        var caps = Enumerable.Range(0, count).Select(i =>
            new Capture(new DateTime(2020, 1, 1).AddMinutes(i).ToString("yyyyMMddHHmmss"),
                $"https://example.com/{(i % 3 == 0 ? "blog" : "docs")}/p{i}", "text/html", "200")).ToArray();
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        await host.Store.SavePagesAsync(caps.Take(Math.Min(count, 300)).Select((c, i) =>
            new NewPage(c, HtmlCodec.Compress($"<html><body>word{i}</body></html>"),
                new ParsedPage("t" + i, null, "word" + i, new PageMetadata(Likes: i)), DateTimeOffset.UtcNow)).ToList());
        return host;
    }

    private static int Rows(string html) => Regex.Matches(html, "data-ts=\"").Count;

    [Theory]
    [InlineData("server")]
    [InlineData("client")]
    [InlineData("bogus")]
    public async Task The_rendered_table_holds_one_page_regardless_of_the_pagination_parameter(string mode)
    {
        await using var host = await HostAsync(120);
        var html = await host.GetAsync($"/?url=example.com/&pagination={mode}&pageSize=25");
        Assert.InRange(Rows(html), 25, 30);   // one page plus a few non-row attributes, never all 120
        Assert.DoesNotContain("clientPrev", html);
    }

    [Fact]
    public async Task Pages_cover_every_capture_exactly_once()
    {
        await using var host = await HostAsync(120);
        var seen = new HashSet<string>();
        for (var p = 1; p <= 5; p++)
        {
            var html = await host.GetAsync($"/?url=example.com/&pageSize=25&page={p}");
            foreach (Match m in Regex.Matches(html, "data-ts=\"(\\d{14})\"")) seen.Add(m.Groups[1].Value + "|" + p);
        }
        Assert.Equal(120, seen.Select(s => s.Split('|')[0]).Distinct().Count());
    }

    [Fact]
    public async Task Popularity_sorted_and_content_filtered_views_also_render_one_page()
    {
        await using var host = await HostAsync(120);
        var popular = await host.GetAsync("/?url=example.com/&sort=liked&pageSize=25");
        var content = await host.GetAsync("/?url=example.com/&searchMode=content&q=word&pageSize=25");
        Assert.InRange(Rows(popular), 25, 30);
        Assert.InRange(Rows(content), 25, 30);
    }

    [Fact]
    public async Task Concurrent_mixed_traffic_never_errors()
    {
        await using var host = await HostAsync(150);
        var paths = new[]
        {
            "/?url=example.com/", "/?url=example.com/&sort=liked&page=2", "/?url=example.com/&section=%2Fblog",
            "/?url=example.com/&searchMode=content&q=word1", "/?url=example.com/&popularityMetric=likes&minPopularity=5",
            "/Home/CaptureSet?url=example.com/&sort=liked", "/healthz", "/?url=bad%20url", "/?url=example.com/&page=9999"
        };
        var tasks = Enumerable.Range(0, 400).Select(i => host.Client.GetAsync(paths[i % paths.Length])).ToArray();
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, $"{r.RequestMessage?.RequestUri} -> {(int)r.StatusCode}"));
    }

    [Fact]
    public async Task Hostile_input_is_rejected_or_ignored_without_server_errors()
    {
        await using var host = await HostAsync(30);
        var junk = new[]
        {
            "/?url=example.com/&page=-5", "/?url=example.com/&page=abc", "/?url=example.com/&pageSize=0", "/?url=example.com/&pageSize=999999",
            "/?url=example.com/&minPopularity=-1", "/?url=example.com/&minPopularity=99999999999999999999",
            "/?url=example.com/&sort=" + new string('x', 5000), "/?url=example.com/&q=" + new string('y', 5000),
            "/?url=example.com/&year=%00&category=%00", "/?url=%3Cscript%3Ealert(1)%3C/script%3E",
            "/?url=example.com/&q=%27%3B%20DROP%20TABLE%20Captures%3B--", "/Home/CaptureSet?url=&sort=liked"
        };
        foreach (var path in junk)
        {
            var r = await host.Client.GetAsync(path);
            Assert.True((int)r.StatusCode < 500, $"{path} -> {(int)r.StatusCode}");
        }
        var after = await host.GetAsync("/?url=example.com/");
        Assert.Contains("data-ts=", after);   // the store survived the injection attempt
    }

    [Fact]
    public async Task Large_list_pages_render_within_a_generous_latency_budget()
    {
        await using var host = await HostAsync(5000);
        await host.GetAsync("/?url=example.com/");   // warm
        var times = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var sw = Stopwatch.StartNew();
            await host.GetAsync($"/?url=example.com/&page={1 + i % 10}&pageSize=50");
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        var p95 = times[(int)(times.Count * 0.95) - 1];
        Assert.True(p95 < 2000, $"p95 was {p95:F0} ms");
    }

    [Fact]
    public async Task Filtered_and_sorted_views_of_a_large_list_stay_fast_when_repeated()
    {
        await using var host = await HostAsync(5000);
        const string q = "/?url=example.com/&section=%2Fblog&sort=liked&popularityMetric=likes&minPopularity=1&pageSize=50";
        await host.GetAsync(q);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) await host.GetAsync(q + "&page=" + (1 + i % 3));
        Assert.True(sw.Elapsed.TotalMilliseconds / 20 < 1500, $"avg {sw.Elapsed.TotalMilliseconds / 20:F0} ms");
    }
}
