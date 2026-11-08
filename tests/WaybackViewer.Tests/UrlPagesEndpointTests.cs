using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class UrlPagesEndpointTests
{
    [Fact]
    public async Task Urls_page_renders_only_one_page_of_rows_and_keyset_next_link_works()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        for (var i = 0; i < 7; i++)
            await store.SaveSearchAsync($"site{i}.com/", "html200", SampleData.Captures(i + 1), DateTimeOffset.UtcNow.AddMinutes(i));

        var first = await host.GetAsync("/urls?sort=az&pageSize=3");
        Assert.Equal(3, Regex.Matches(first, "<tr data-url-id=").Count);
        Assert.Contains("site0.com/", first);
        Assert.DoesNotContain("site5.com/", first);

        var next = Regex.Match(first, "href=\"([^\"]*after=[^\"]*)\"").Groups[1].Value.Replace("&amp;", "&");
        var second = await host.GetAsync(next + "&pageSize=3");
        Assert.Contains("site3.com/", second);
        Assert.DoesNotContain("site0.com/", second);
    }

    [Fact]
    public async Task Detail_page_is_server_paginated_and_validates_input()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var snap = await store.SaveSearchAsync("a.com/", "html200", SampleData.Captures(12), DateTimeOffset.UtcNow);

        var html = await host.GetAsync($"/urls/{snap.SearchId}?pageSize=5&page=3");
        Assert.Equal(2, Regex.Matches(html, "<tr data-ts=").Count);
        Assert.Contains("Page 3 of 3", html);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/urls/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/urls/{snap.SearchId}?from=notadate")).StatusCode);
    }

    [Fact]
    public async Task Suggest_requires_min_chars_and_returns_ranked_json()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        await store.SaveSearchAsync("example.com/a/", "html200", SampleData.Captures(2), DateTimeOffset.UtcNow);
        await store.SaveSearchAsync("example.com/b/", "html200", SampleData.Captures(7), DateTimeOffset.UtcNow);

        var all = await host.GetAsync("/urls/suggest");   // empty term: every captured URL
        Assert.Contains("example.com/a/", all); Assert.Contains("example.com/b/", all);
        var narrowed = await host.GetAsync("/urls/suggest?q=com%2Fa");   // matches anywhere in the URL
        Assert.Contains("example.com/a/", narrowed); Assert.DoesNotContain("example.com/b/", narrowed);
        var body = await host.GetAsync("/urls/suggest?q=example");
        Assert.True(body.IndexOf("example.com/b/", StringComparison.Ordinal) < body.IndexOf("example.com/a/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Home_autocomplete_shows_urls_only_not_capture_counts()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");
        Assert.Contains("/urls/suggest?q=", html);
        Assert.Contains("o.value = s.prefix;", html);
        Assert.DoesNotContain("o.label", html);
    }

    [Fact]
    public async Task Nav_links_every_section_and_about_page_credits_the_archive()
    {
        await using var host = await AppHost.StartAsync();
        var page = await host.GetAsync("/operations");
        Assert.Contains("href=\"/urls\"", page);
        Assert.Contains("href=\"/about\"", page);

        var about = await host.GetAsync("/about");
        Assert.Contains("Internet Archive", about);
        Assert.Contains("Version", about);
    }
}
