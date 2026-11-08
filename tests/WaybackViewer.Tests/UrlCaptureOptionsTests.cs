using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class UrlCaptureOptionsTests
{
    private static readonly Capture[] Mixed =
    {
        new("20200101000000", "https://a.com/1", "text/html", "200"),
        new("20200102000000", "https://a.com/2", "image/png", "404"),
        new("20200103000000", "https://a.com/3", "text/html", "301")
    };

    [Fact]
    public async Task Store_returns_distinct_sorted_mime_and_status_values_of_that_search_only()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", Mixed, DateTimeOffset.UtcNow);
        await store.SaveSearchAsync("b.com/", "html200",
            new[] { new Capture("20210101000000", "https://b.com/x", "application/pdf", "503") }, DateTimeOffset.UtcNow);

        var options = await store.GetUrlCaptureOptionsAsync(snap.SearchId);

        Assert.NotNull(options);
        Assert.Equal(new[] { "image/png", "text/html" }, options!.Mimes);
        Assert.Equal(new[] { "200", "301", "404" }, options.Statuses);
    }

    [Fact]
    public async Task Detail_dropdowns_list_values_present_in_the_captures_and_keep_the_current_selection()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", Mixed, DateTimeOffset.UtcNow);

        var html = await host.GetAsync($"/urls/{snap.SearchId}");
        Assert.Contains("<option value=\"image/png\"", html);
        Assert.Contains("<option value=\"404\"", html);
        Assert.DoesNotContain("<option value=\"application/pdf\"", html);
        Assert.DoesNotContain("<option value=\"503\"", html);

        var chosen = await host.GetAsync($"/urls/{snap.SearchId}?mime=application%2Fx-gone&status=418");
        Assert.Contains("<option value=\"application/x-gone\" selected", chosen);
        Assert.Contains("<option value=\"418\" selected", chosen);
    }

    [Fact]
    public async Task Responsive_columns_use_classes_not_positional_selectors()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", Mixed, DateTimeOffset.UtcNow);
        await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

        var ops = await host.GetAsync("/operations");
        Assert.DoesNotContain("nth-child", ops);
        Assert.Contains(".hide-md { display: none; }", ops);
        Assert.Contains("<th class=\"hide-lg\">ETA</th>", ops);
        Assert.Contains("<col class=\"hide-md\"", ops);

        var list = await host.GetAsync("/urls");
        Assert.Contains("<th class=\"hide-md\">Last refresh</th>", list);

        var detail = await host.GetAsync($"/urls/{snap.SearchId}");
        Assert.Contains("<th class=\"hide-sm\">Saved</th>", detail);
    }
}
