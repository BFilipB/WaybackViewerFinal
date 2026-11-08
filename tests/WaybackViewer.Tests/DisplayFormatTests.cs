using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class DisplayFormatTests
{
    [Theory]
    [InlineData("20200728180718", "2020-07-28")]
    [InlineData("20261107054657", "2026-11-07")]
    [InlineData("20200728", "2020-07-28")]
    [InlineData(null, "\u2013")]
    [InlineData("", "\u2013")]
    [InlineData("garbage", "garbage")]
    public void Timestamp_is_formatted_cleanly(string? input, string expected) =>
        Assert.Equal(expected, DisplayFormat.Timestamp(input));

    [Fact]
    public void Number_and_percent_are_grouped_and_clamped()
    {
        Assert.Equal("376,352", DisplayFormat.Number(376352));
        Assert.Equal("3.8%", DisplayFormat.Percent(14457, 376352));
        Assert.Equal("100%", DisplayFormat.Percent(5, 3));
        Assert.Equal("", DisplayFormat.Percent(0, 0));
    }

    [Theory]
    [InlineData("CaptureLoad", "Capture list")]
    [InlineData("PagePreload", "Page preload")]
    public void Operation_kind_labels_are_readable(string kind, string label) =>
        Assert.Equal(label, DisplayFormat.OperationKindLabel(kind));
}

public class TableLayoutEndpointTests
{
    [Fact]
    public async Task Urls_pages_show_formatted_dates_and_workflow_bars()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(3), DateTimeOffset.UtcNow);

        var list = await host.GetAsync("/urls");
        Assert.DoesNotMatch(new Regex(@"<td>\d{14}</td>"), list);
        Assert.Matches(new Regex(@"<td( class=""hide-\w+"")?>\d{4}-\d{2}-\d{2}</td>"), list);

        var detail = await host.GetAsync($"/urls/{snap.SearchId}");
        Assert.Matches(new Regex(@"<td title=""\d{14}"">\d{4}-\d{2}-\d{2}</td>"), detail);
        Assert.DoesNotContain("class=\"table-scroll\"><table style", detail);
    }

    [Fact]
    public async Task Operations_page_keeps_all_actions_in_one_fixed_layout_table()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(3), DateTimeOffset.UtcNow);
        await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

        var html = await host.GetAsync("/operations");
        Assert.Contains("table-layout: fixed", html);
        Assert.Contains("<colgroup>", html);
        Assert.Contains("#opsTable td.ops-actions { white-space: nowrap", html);
        Assert.Single(Regex.Matches(html, @"data-delete=""all"""));
        Assert.NotNull(snap);
    }

    [Fact]
    public async Task Layout_has_responsive_table_rules_and_pages_show_no_time_of_day()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(3), DateTimeOffset.UtcNow);

        var list = await host.GetAsync("/urls");
        Assert.Contains("@media (max-width: 1250px)", list);
        Assert.DoesNotMatch(new Regex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}"), list);
        var detail = await host.GetAsync($"/urls/{snap.SearchId}");
        Assert.DoesNotMatch(new Regex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}"), detail);
    }
}
