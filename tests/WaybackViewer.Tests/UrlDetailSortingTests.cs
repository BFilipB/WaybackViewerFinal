using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class UrlDetailSortingTests
{
    [Fact]
    public void Sort_whitelist_rejects_unknown_columns()
    {
        Assert.Equal("date", CaptureSorts.Normalize(null));
        Assert.Equal("date", CaptureSorts.Normalize("c.Id; DROP TABLE Captures"));
        Assert.Equal("url", CaptureSorts.Normalize("url"));
        Assert.Equal("c.CaptureTs", CaptureSorts.Column("anything-else"));
    }

    [Fact]
    public async Task Store_sorts_captures_in_both_directions_across_the_whole_result()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(6), DateTimeOffset.UtcNow);

        var desc = await store.GetSearchCapturesPageAsync(snap.SearchId, new UrlCaptureFilter(null, null, null, null, "url", true), 0, 6);
        var asc = await store.GetSearchCapturesPageAsync(snap.SearchId, new UrlCaptureFilter(null, null, null, null, "url", false), 0, 6);

        Assert.Equal(6, asc.Items.Count);
        Assert.Equal(asc.Items.Select(i => i.Capture.Original).Reverse(), desc.Items.Select(i => i.Capture.Original));
        Assert.Equal(asc.Items.Select(i => i.Capture.Original).OrderBy(x => x, StringComparer.Ordinal), asc.Items.Select(i => i.Capture.Original));
    }

    [Fact]
    public async Task Detail_page_has_sortable_headers_and_first_last_pager_and_ignores_bad_sort()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(12), DateTimeOffset.UtcNow);

        var middle = await host.GetAsync($"/urls/{snap.SearchId}?pageSize=5&page=2&sort=url&dir=asc");
        Assert.Contains("sort=url&amp;dir=desc", middle);
        Assert.Contains("&laquo; First", middle);
        Assert.Contains("Last &raquo;", middle);
        Assert.Equal(5, Regex.Matches(middle, "<tr data-ts=").Count);

        var last = await host.GetAsync($"/urls/{snap.SearchId}?pageSize=5&page=3");
        Assert.DoesNotContain("Last &raquo;", last);
        Assert.Contains("&laquo; First", last);

        var bad = await host.Client.GetAsync($"/urls/{snap.SearchId}?sort=Id%3BDROP&dir=sideways");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
    }

    [Fact]
    public async Task Operations_page_no_longer_swallows_poll_errors()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(2), DateTimeOffset.UtcNow);
        await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

        var html = await host.GetAsync("/operations");
        Assert.DoesNotContain("catch { /*", html);
        Assert.Contains("Connection lost - retrying", html);
    }
}
