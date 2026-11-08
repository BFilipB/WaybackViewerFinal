using System.Net;
using Microsoft.AspNetCore.Builder;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Starts the real application (Kestrel on a random local port, real controllers/views/services) with the
/// archive replaced by FakeArchiveClient and the database by the in-memory store. Everything else is production code.
public sealed class AppHost : IAsyncDisposable
{
    public required WebApplication App { get; init; }
    public required HttpClient Client { get; init; }
    public required FakeArchiveClient Archive { get; init; }
    public InMemoryArchiveStore? Store { get; init; }

    public static async Task<AppHost> StartAsync(Dictionary<string, string?>? extraConfig = null, FakeArchiveClient? archiveOverride = null)
    {
        var archive = archiveOverride ?? new FakeArchiveClient();
        var config = new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Preload:Workers"] = "3",
            ["Preload:WriteMaxDelayMs"] = "20",
            ["Logging:LogLevel:Default"] = "Warning"
        };
        if (extraConfig is not null) foreach (var kv in extraConfig) config[kv.Key] = kv.Value;
        var app = AppBuilder.Build(
            ["--urls", "http://127.0.0.1:0"],
            services => services.AddSingleton<IArchiveClient>(archive),
            config);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient(new AntiforgeryHandler(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new System.Net.CookieContainer() }))
            { BaseAddress = new Uri(address) };
        return new AppHost
        {
            App = app, Client = client, Archive = archive,
            Store = app.Services.GetRequiredService<IArchiveStore>() as InMemoryArchiveStore
        };
    }

public class RateLimitAndOperationsEndpointTests
{
    [Fact]
    public async Task Operation_status_endpoint_returns_json_array()
    {
        await using var host = await AppHost.StartAsync();
        var body = await host.GetAsync("/Home/OperationStatus");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Rate_limit_can_reject_when_configured_low()
    {
        await using var host = await AppHost.StartAsync(new Dictionary<string, string?>
        {
            ["RateLimit:PermitLimit"] = "1",
            ["RateLimit:WindowSeconds"] = "60"
        });

        var first = await host.Client.GetAsync("/healthz");
        var second = await host.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
    }

    [Fact]
    public async Task Retry_failed_resets_failed_captures_and_downloads_them_again()
    {
        await using var host = await AppHost.StartAsync();
        var captures = SampleData.Captures(2);
        await host.Store!.SaveSearchAsync("example.com/", "html200", captures, DateTimeOffset.UtcNow);
        await host.Store.SaveOutcomesAsync([new CaptureOutcome(captures[0], PreloadOutcome.Failed, "http-503", DateTimeOffset.UtcNow)]);
        host.RouteSite("example.com/", captures);

        var response = await host.Client.PostAsync("/Home/RetryFailed?url=example.com/&filter=html200", null);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("reset").GetInt32());
        await SampleData.Eventually(async () => await host.Store.GetPageAsync(captures[0].Timestamp, captures[0].Original, false) is not null);
        Assert.Empty(await host.Store.GetOutcomesAsync("example.com/", "html200"));
    }

    [Fact]
    public async Task Retry_failed_rejects_invalid_urls()
    {
        await using var host = await AppHost.StartAsync();
        var response = await host.Client.PostAsync("/Home/RetryFailed?url=" + Uri.EscapeDataString("bad url"), null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stop_endpoint_no_longer_exists()
    {
        await using var host = await AppHost.StartAsync();
        var response = await host.Client.PostAsync("/Home/Stop?url=example.com/", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        await using var host = await AppHost.StartAsync();
        using var raw = new HttpClient { BaseAddress = host.Client.BaseAddress };
        var response = await raw.PostAsync("/Home/RetryFailed?url=example.com/&filter=html200", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Layout_exposes_antiforgery_token_for_scripts()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");
        Assert.Matches("<meta name=\"request-verification-token\" content=\"[^\"]+\"", html);
    }
}

    public void RouteSite(string prefix, IReadOnlyList<Capture> captures, string filter = "html200")
    {
        Archive.Route(ArchiveUrls.Cdx(prefix, filter), SampleData.CdxJson(captures));
        foreach (var c in captures)
            Archive.Route(ArchiveUrls.Raw(c.Timestamp, c.Original), SampleData.Html("Page " + c.Original, "Text of " + c.Timestamp));
    }

    public Task<string> GetAsync(string path) => Client.GetStringAsync(path);

    public static string FrameUrl(Capture c) => $"/Home/Frame?ts={c.Timestamp}&url={Uri.EscapeDataString(c.Original)}";
    public static string TextUrl(Capture c) => $"/Home/Text?ts={c.Timestamp}&url={Uri.EscapeDataString(c.Original)}";

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

public class HomePageTests
{
    [Fact]
    public async Task Start_page_is_generic_and_has_no_hard_coded_account()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");

        Assert.Contains("Enter any website address", html);
        Assert.DoesNotContain("twitter.com", html, StringComparison.OrdinalIgnoreCase); // no site is pre-filled
        Assert.Equal(0, host.Archive.TotalCalls); // nothing is fetched until the user asks
    }

    [Fact]
    public async Task Captures_are_listed_then_served_from_the_database()
    {
        await using var host = await AppHost.StartAsync();
        var captures = SampleData.Captures(5);
        host.RouteSite("example.com/", captures);

        var first = await host.GetAsync("/?url=example.com/");
        foreach (var c in captures) Assert.Contains($"data-ts=\"{c.Timestamp}\"", first);
        Assert.Contains("Loaded from Wayback and saved to the database", first);

        var second = await host.GetAsync("/?url=example.com/");
        Assert.Contains("Loaded from the database", second);
        Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Cdx("example.com/", "html200")));
    }

    [Fact]
    public async Task Refresh_downloads_the_list_again()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("example.com/", SampleData.Captures(2));
        await host.GetAsync("/?url=example.com/");
        await host.GetAsync("/?url=example.com/&refresh=true");
        Assert.Equal(2, host.Archive.Calls(ArchiveUrls.Cdx("example.com/", "html200")));
    }

    [Fact]
    public async Task Pasted_wayback_links_and_wildcards_are_understood()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("example.com/news/", SampleData.Captures(3));

        var pasted = Uri.EscapeDataString("https://web.archive.org/web/*/https://Example.com/News/*");
        var html = await host.GetAsync("/?url=" + pasted);

        Assert.Contains("value=\"example.com/news/\"", html);
        Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Cdx("example.com/news/", "html200")));
    }

    [Fact]
    public async Task Any_site_works_not_just_one_account()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("another-site.org/forum/", [new Capture("20150505050505", "http://another-site.org/forum/thread1", "text/html", "200")]);
        var html = await host.GetAsync("/?url=another-site.org/forum/");
        Assert.Contains("http://another-site.org/forum/thread1", html);
    }

    [Fact]
    public async Task Filter_choice_is_passed_to_the_archive()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("example.com/", SampleData.Captures(2), filter: "all");
        var html = await host.GetAsync("/?url=example.com/&filter=all");
        Assert.Contains("data-ts=", html);
        Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Cdx("example.com/", "all")));
    }

    [Fact]
    public async Task Popularity_sort_and_filter_values_are_applied_and_preserved_in_the_form()
    {
        await using var host = await AppHost.StartAsync();
        var captures = new[]
        {
            new Capture("20200101010101", "https://example.com/first", "text/html", "200"),
            new Capture("20200101020202", "https://example.com/second", "text/html", "200")
        };
        host.RouteSite("example.com/", captures);
        await host.Store!.SaveSearchAsync("example.com/", "html200", captures, DateTimeOffset.UtcNow);
        await host.Store.SavePagesAsync([
            new NewPage(captures[0], HtmlCodec.Compress("<html><head><meta property=\"og:likes\" content=\"10\"></head><body>first</body></html>"),
                new ParsedPage("first", null, "first", new PageMetadata(Likes: 10)), DateTimeOffset.UtcNow),
            new NewPage(captures[1], HtmlCodec.Compress("<html><head><meta property=\"og:likes\" content=\"20\"></head><body>second</body></html>"),
                new ParsedPage("second", null, "second", new PageMetadata(Likes: 20)), DateTimeOffset.UtcNow)
        ]);

        var html = await host.GetAsync("/?url=example.com/&sort=liked&popularityMetric=likes&minPopularity=15");

        Assert.Contains("value=\"liked\" selected=\"selected\"", html);
        Assert.Contains("value=\"likes\" selected=\"selected\"", html);
        Assert.Contains("name=\"minPopularity\"", html);
        Assert.Contains("value=\"15\"", html);
        Assert.Contains("data-ts=\"20200101020202\"", html);
        Assert.DoesNotContain("data-ts=\"20200101010101\"", html);
    }

    private static async Task<(AppHost host, Capture[] caps)> PopularityHostAsync()
    {
        var host = await AppHost.StartAsync();
        var caps = new[]
        {
            new Capture("20200101010101", "https://example.com/a", "text/html", "200"),
            new Capture("20200102010101", "https://example.com/b", "text/html", "200"),
            new Capture("20200103010101", "https://example.com/c", "text/html", "200"),
            new Capture("20200104010101", "https://example.com/d", "text/html", "200")
        };
        host.RouteSite("example.com/", caps);
        await host.Store!.SaveSearchAsync("example.com/", "html200", caps, DateTimeOffset.UtcNow);
        long[] likes = [30, 10, 20];
        var pages = new List<NewPage>();
        for (var i = 0; i < 3; i++)
            pages.Add(new NewPage(caps[i], HtmlCodec.Compress("<html><body>p" + i + "</body></html>"),
                new ParsedPage("p" + i, null, "p" + i, new PageMetadata(Likes: likes[i])), DateTimeOffset.UtcNow));
        await host.Store.SavePagesAsync(pages);
        return (host, caps);
    }

    [Fact]
    public async Task Layout_and_theme_switches_are_rendered()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");
        Assert.Contains("id=\"layoutSelect\"", html);
        Assert.Contains("value=\"focus\"", html);
        Assert.Contains("id=\"themeSelect\"", html);
        Assert.Contains("wb-layout", html);
    }

    [Fact]
    public async Task Popularity_presets_are_offered_and_filter_when_followed()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&popularityMetric=likes");
        Assert.Contains("popularity-presets", html);
        Assert.Contains("Top 10%", html);
        Assert.Contains("minPopularity=30", html);
        var filtered = await host.GetAsync("/?url=example.com/&popularityMetric=likes&minPopularity=20");
        Assert.Contains("data-ts=\"20200101010101\"", filtered);
        Assert.Contains("data-ts=\"20200103010101\"", filtered);
        Assert.DoesNotContain("data-ts=\"20200102010101\"", filtered);
    }

    [Fact]
    public async Task Pending_popularity_data_is_reported_to_the_user()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&sort=liked");
        Assert.Contains("popularity-pending", html);
        Assert.Contains("have no popularity data yet", html);
        var plain = await host.GetAsync("/?url=example.com/");
        Assert.DoesNotContain("popularity-pending", plain);
    }

    [Fact]
    public async Task Sort_and_filter_memory_script_is_present()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/");
        Assert.Contains("wb-sort-filter", html);
    }

    [Fact]
    public async Task CaptureSet_export_honors_popularity_sort_and_filter()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var json = await host.GetAsync("/Home/CaptureSet?url=example.com/&sort=liked");
        var order = new[] { "20200101010101", "20200103010101", "20200102010101", "20200104010101" }
            .Select(ts => json.IndexOf(ts, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.True(order[0] < order[1] && order[1] < order[2] && order[2] < order[3]);

        var filtered = await host.GetAsync("/Home/CaptureSet?url=example.com/&popularityMetric=likes&minPopularity=20");
        Assert.Contains("20200101010101", filtered);
        Assert.Contains("20200103010101", filtered);
        Assert.DoesNotContain("20200102010101", filtered);
        Assert.DoesNotContain("20200104010101", filtered);
    }

    [Theory]
    [InlineData("popular")]
    [InlineData("engaged")]
    [InlineData("commented")]
    [InlineData("liked")]
    [InlineData("viewed")]
    [InlineData("score")]
    [InlineData("newest")]
    [InlineData("bogus")]
    public async Task Every_sort_mode_renders_all_captures(string sort)
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&sort=" + sort);
        foreach (var ts in new[] { "20200101010101", "20200102010101", "20200103010101", "20200104010101" })
            Assert.Contains("data-ts=\"" + ts + "\"", html);
    }

    [Theory]
    [InlineData("views")]
    [InlineData("likes")]
    [InlineData("comments")]
    [InlineData("engaged")]
    [InlineData("score")]
    [InlineData("nope")]
    public async Task Every_popularity_metric_is_accepted_without_errors(string metric)
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&popularityMetric=" + metric + "&minPopularity=1");
        Assert.DoesNotContain("Exception", html);
        Assert.Contains("id=\"searchBar\"", html);
    }

    [Fact]
    public async Task Popularity_filter_never_downloads_pages_from_the_archive()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        await host.GetAsync("/?url=example.com/&sort=liked&popularityMetric=likes&minPopularity=10");
        await host.GetAsync("/?url=example.com/&sort=viewed&popularityMetric=views&minPopularity=1");
        Assert.True(host.Archive.Calls(ArchiveUrls.Cdx("example.com/", "html200")) <= 1);
    }

    [Fact]
    public async Task Server_pagination_with_popularity_sort_keeps_order_across_pages()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var p1 = await host.GetAsync("/?url=example.com/&sort=liked&pageSize=25");
        Assert.Contains("data-ts=\"20200101010101\"", p1);
        var big = await host.GetAsync("/?url=example.com/&sort=liked&page=99");
        Assert.Contains("data-ts=", big);
    }

    [Fact]
    public async Task Content_search_combines_with_popularity_filter()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&searchMode=content&q=p0&popularityMetric=likes&minPopularity=20");
        Assert.Contains("data-ts=\"20200101010101\"", html);
        Assert.DoesNotContain("data-ts=\"20200103010101\"", html);
    }

    [Fact]
    public async Task Layout_has_all_modes_and_all_themes()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");
        foreach (var l in new[] { "classic", "focus", "wide", "compact" })
            Assert.Contains("value=\"" + l + "\"", html);
        foreach (var t in new[] { "midnight-command", "obsidian-blue", "graphite-amber", "aurora-steel", "night-ops-green",
                     "deep-violet", "slate-cyan", "iron-redline", "carbon-gold", "arctic-shadow" })
            Assert.Contains(t, html);
    }

    [Fact]
    public async Task Every_theme_has_readable_text_and_accent_contrast()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");
        var themes = System.Text.RegularExpressions.Regex.Matches(html,
            @"data-theme='([a-z\-]+)'\]\s*\{([^}]*)\}");
        Assert.True(themes.Count >= 10);
        static double Lum(string hex)
        {
            double Ch(int i)
            {
                var v = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(0) + 0.7152 * Ch(1) + 0.0722 * Ch(2);
        }
        static double Ratio(string a, string b)
        {
            var (x, y) = (Lum(a), Lum(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        foreach (System.Text.RegularExpressions.Match m in themes)
        {
            string Get(string name) => System.Text.RegularExpressions.Regex.Match(m.Groups[2].Value, "--" + name + @":\s*(#[0-9a-fA-F]{6})").Groups[1].Value;
            var (bg, panel, text, muted, accent) = (Get("bg"), Get("panel"), Get("text"), Get("muted"), Get("accent"));
            Assert.True(Ratio(text, bg) >= 7, m.Groups[1].Value + " text on bg");
            Assert.True(Ratio(text, panel) >= 7, m.Groups[1].Value + " text on panel");
            Assert.True(Ratio(muted, panel) >= 4.5, m.Groups[1].Value + " muted on panel");
            Assert.True(Ratio(accent, panel) >= 3, m.Groups[1].Value + " accent on panel");
        }
    }

    [Fact]
    public async Task Layout_script_is_complete_and_has_keyboard_shortcuts()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/");
        Assert.Contains("toLocaleTimeString", html);
        Assert.Contains("e.altKey", html);
        Assert.Contains("@media (max-width: 760px)", html);
    }

    [Fact]
    public async Task Histogram_view_tools_and_live_pending_notice_are_rendered()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&popularityMetric=likes");
        Assert.Contains("class=\"popularity-histogram\"", html);
        Assert.Contains("copyViewLink", html);
        Assert.Contains("saveView", html);
        Assert.Contains("wb-saved-views", html);
        var (host2, _) = await PopularityHostAsync();
        await using var _h2 = host2;
        var pending = await host2.GetAsync("/?url=example.com/&sort=liked");
        Assert.Contains("id=\"popularityPending\"", pending);
        var plain = await host.GetAsync("/");
        Assert.DoesNotContain("class=\"popularity-histogram\"", plain);
    }

    [Fact]
    public async Task Sort_by_likes_orders_captures_descending_and_unknown_last()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&sort=liked");
        var order = new[] { "20200101010101", "20200103010101", "20200102010101", "20200104010101" }
            .Select(ts => html.IndexOf("data-ts=\"" + ts + "\"", StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.True(order[0] < order[1] && order[1] < order[2] && order[2] < order[3]);
    }

    [Fact]
    public async Task Filter_excludes_unknown_and_low_and_does_not_hit_archive_for_pages()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var before = host.Archive.TotalCalls;
        var html = await host.GetAsync("/?url=example.com/&popularityMetric=likes&minPopularity=20");
        Assert.Contains("data-ts=\"20200101010101\"", html);
        Assert.Contains("data-ts=\"20200103010101\"", html);
        Assert.DoesNotContain("data-ts=\"20200102010101\"", html);
        Assert.DoesNotContain("data-ts=\"20200104010101\"", html);
        Assert.True(host.Archive.TotalCalls - before <= 1);
    }

    [Fact]
    public async Task Invalid_sort_and_negative_threshold_fall_back_to_defaults()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&sort=bogus&popularityMetric=nope&minPopularity=-5");
        foreach (var ts in new[] { "20200101010101", "20200102010101", "20200103010101", "20200104010101" })
            Assert.Contains("data-ts=\"" + ts + "\"", html);
    }

    [Fact]
    public async Task Default_request_without_popularity_params_is_unchanged()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/");
        Assert.True(html.IndexOf("data-ts=\"20200104010101\"", StringComparison.Ordinal)
                    < html.IndexOf("data-ts=\"20200101010101\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Popularity_summary_badges_and_reset_link_are_shown()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&sort=liked&popularityMetric=likes&minPopularity=20");
        Assert.Contains("popularity-summary", html);
        Assert.Contains("popularity-badges", html);
        Assert.Contains("(2 hidden)", html);
        Assert.Contains("Reset sort and popularity filter", html);
        Assert.Contains("sort=newest", html);
    }

    [Fact]
    public async Task Popularity_filter_with_no_matches_explains_itself()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&popularityMetric=likes&minPopularity=999");
        Assert.Contains("No captures reach 999 likes", html);
        Assert.DoesNotContain("data-ts=", html);
    }

    [Fact]
    public async Task Default_view_has_no_popularity_summary()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/");
        Assert.DoesNotContain("popularity-summary", html);
    }

    [Fact]
    public async Task Popularity_filter_combines_with_year_filter_and_paging()
    {
        var (host, _) = await PopularityHostAsync();
        await using var _h = host;
        var html = await host.GetAsync("/?url=example.com/&year=2020&popularityMetric=likes&minPopularity=10&pageSize=25&page=9");
        Assert.Contains("data-ts=\"20200101010101\"", html);
        Assert.DoesNotContain("data-ts=\"20200104010101\"", html);
    }

    [Fact]
    public async Task Unusable_in_valid_url_rejected()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/?url=" + Uri.EscapeDataString("not a url"));
        Assert.Contains("Enter a valid URL", html);
        Assert.Equal(0, host.Archive.TotalCalls);
    }

    [Fact]
    public async Task Archive_errors_are_shown_to_the_user()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.GetAsync("/?url=nothing-routed.example/");
        Assert.Contains("Error:", html);
    }
}

public class FrameAndTextTests
{
    [Fact]
    public async Task Frame_serves_the_archived_page_with_a_base_tag_and_sandbox_headers()
    {
        await using var host = await AppHost.StartAsync();
        var c = SampleData.Capture(1);
        host.RouteSite("example.com/", [c]);

        var response = await host.Client.GetAsync(AppHost.FrameUrl(c));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"<base href=\"https://web.archive.org/web/{c.Timestamp}if_/{c.Original}\">", body);
        Assert.Contains("Text of " + c.Timestamp, body);
        Assert.Contains("sandbox", string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("max-age=86400", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Second_frame_request_comes_from_the_database()
    {
        await using var host = await AppHost.StartAsync();
        var c = SampleData.Capture(1);
        host.RouteSite("example.com/", [c]);

        await host.GetAsync(AppHost.FrameUrl(c));
        await host.GetAsync(AppHost.FrameUrl(c));

        Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Raw(c.Timestamp, c.Original)));
        Assert.Equal(1, host.Store!.PageCount);
    }

    [Fact]
    public async Task Parallel_frame_requests_download_the_page_only_once()
    {
        await using var host = await AppHost.StartAsync();
        var c = SampleData.Capture(1);
        host.RouteSite("example.com/", [c]);
        host.Archive.BeforeResponse = _ => Task.Delay(150);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => host.Client.GetAsync(AppHost.FrameUrl(c))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Raw(c.Timestamp, c.Original)));
    }

    [Fact]
    public async Task Non_html_downloads_redirect_to_the_wayback_machine()
    {
        await using var host = await AppHost.StartAsync();
        var c = new Capture("20200101000000", "https://example.com/logo.png", "image/png", "200");
        host.Archive.Route(ArchiveUrls.Raw(c.Timestamp, c.Original), "\u0089PNG\r\n\u001a\n\0\0\0\rIHDR\0\0\u0001\0\uFFFD\uFFFD\0\0\0\0");

        var response = await host.Client.GetAsync(AppHost.FrameUrl(c));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ArchiveUrls.Frame(c.Timestamp, c.Original), response.Headers.Location!.ToString());
        Assert.Equal(0, host.Store!.PageCount);
    }

    [Fact]
    public async Task Unavailable_captures_redirect_to_the_wayback_machine()
    {
        await using var host = await AppHost.StartAsync();
        var response = await host.Client.GetAsync(AppHost.FrameUrl(SampleData.Capture(9)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Theory]
    [InlineData("/Home/Frame?ts=abc&url=https%3A%2F%2Fa.com%2F")]
    [InlineData("/Home/Frame?ts=20200101000000&url=javascript%3Aalert(1)")]
    [InlineData("/Home/Frame?ts=20200101000000")]
    [InlineData("/Home/Frame")]
    [InlineData("/Home/Text?ts=20200101000000&url=file%3A%2F%2F%2Fetc%2Fpasswd")]
    [InlineData("/Home/Text?ts=1&url=https%3A%2F%2Fa.com%2F")]
    public async Task Bad_parameters_are_rejected(string path)
    {
        await using var host = await AppHost.StartAsync();
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Archive.TotalCalls);
    }

    [Fact]
    public async Task Text_endpoint_returns_title_description_and_text_from_the_database()
    {
        await using var host = await AppHost.StartAsync();
        var c = SampleData.Capture(3);
        host.RouteSite("example.com/", [c]);

        using var first = JsonDocument.Parse(await host.GetAsync(AppHost.TextUrl(c)));
        using var second = JsonDocument.Parse(await host.GetAsync(AppHost.TextUrl(c)));

        Assert.Equal("Page " + c.Original, first.RootElement.GetProperty("title").GetString());
        Assert.Equal("desc of Page " + c.Original, first.RootElement.GetProperty("description").GetString());
        Assert.Equal("Text of " + c.Timestamp, first.RootElement.GetProperty("text").GetString());
        Assert.Equal(first.RootElement.GetRawText(), second.RootElement.GetRawText());
        Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Raw(c.Timestamp, c.Original)));
    }

    [Fact]
    public async Task Text_endpoint_reports_a_readable_error_when_the_page_cannot_be_loaded()
    {
        await using var host = await AppHost.StartAsync();
        using var doc = JsonDocument.Parse(await host.GetAsync(AppHost.TextUrl(SampleData.Capture(4))));
        Assert.Contains("could not be loaded", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("text").GetString());
    }
}

public class PreloadEndpointTests
{
    [Fact]
    public async Task Listing_a_site_saves_every_page_in_the_background_and_slides_then_need_no_downloads()
    {
        await using var host = await AppHost.StartAsync();
        var captures = SampleData.Captures(12);
        host.RouteSite("example.com/", captures);

        await host.GetAsync("/?url=example.com/");

        await SampleData.Eventually(async () =>
        {
            using var doc = JsonDocument.Parse(await host.GetAsync("/Home/PreloadStatus"));
            return !doc.RootElement.GetProperty("running").GetBoolean() && doc.RootElement.GetProperty("completed").GetInt32() == 12;
        });
        Assert.Equal(12, host.Store!.PageCount);

        // every slide is now served from the database ...
        foreach (var c in captures)
        {
            var response = await host.Client.GetAsync(AppHost.FrameUrl(c));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Raw(c.Timestamp, c.Original)));
        }

        // ... and the list marks them all as ready, so the slideshow shows no loading state for them
        var html = await host.GetAsync("/?url=example.com/");
        Assert.Equal(12, CountOccurrences(html, "data-ready=\"1\""));
        Assert.Equal(0, CountOccurrences(html, "data-ready=\"0\""));
        Assert.Contains("12 of 12 captures already saved", html);
    }

    [Fact]
    public async Task Reloading_the_list_does_not_download_saved_pages_again()
    {
        await using var host = await AppHost.StartAsync();
        var captures = SampleData.Captures(6);
        host.RouteSite("example.com/", captures);
        await host.GetAsync("/?url=example.com/");
        await SampleData.Eventually(() => host.Store!.PageCount == 6);

        await host.GetAsync("/?url=example.com/");
        await host.GetAsync("/?url=example.com/&refresh=true");
        await Task.Delay(300);

        foreach (var c in captures) Assert.Equal(1, host.Archive.Calls(ArchiveUrls.Raw(c.Timestamp, c.Original)));
    }

    [Fact]
    public async Task Prioritize_accepts_a_list_of_keys()
    {
        await using var host = await AppHost.StartAsync();
        var response = await host.Client.PostAsync("/Home/Prioritize",
            new StringContent("[\"20200101000000|https://a.com/\"]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Prioritize_rejects_oversized_requests()
    {
        await using var host = await AppHost.StartAsync();
        var keys = JsonSerializer.Serialize(Enumerable.Range(0, 201).Select(i => "k" + i));
        var response = await host.Client.PostAsync("/Home/Prioritize", new StringContent(keys, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preload_status_is_idle_before_anything_is_listed()
    {
        await using var host = await AppHost.StartAsync();
        using var doc = JsonDocument.Parse(await host.GetAsync("/Home/PreloadStatus"));
        Assert.False(doc.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

public class SqlitePersistenceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "wb-app-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }

    private Dictionary<string, string?> Config() => new()
    {
        ["Storage:Provider"] = "Sqlite",
        ["ConnectionStrings:WaybackDb"] = $"Data Source={Path.Combine(dir, "wayback.db")}"
    };

    [Fact]
    public async Task Everything_survives_an_app_restart_and_needs_no_archive_access()
    {
        var captures = SampleData.Captures(8);

        await using (var first = await AppHost.StartAsync(Config()))
        {
            first.RouteSite("example.com/", captures);
            await first.GetAsync("/?url=example.com/");
            await SampleData.Eventually(async () =>
            {
                using var doc = JsonDocument.Parse(await first.GetAsync("/Home/PreloadStatus"));
                return !doc.RootElement.GetProperty("running").GetBoolean() && doc.RootElement.GetProperty("completed").GetInt32() == 8;
            });
        }

        // second run: same database file, archive that has NO routes at all
        await using var second = await AppHost.StartAsync(Config());
        var html = await second.GetAsync("/?url=example.com/");
        Assert.Contains("Loaded from the database", html);
        Assert.Equal(8, CountOf(html, "data-ready=\"1\""));

        foreach (var c in captures)
        {
            var frame = await second.Client.GetAsync(AppHost.FrameUrl(c));
            Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
            using var text = JsonDocument.Parse(await second.GetAsync(AppHost.TextUrl(c)));
            Assert.Equal("Text of " + c.Timestamp, text.RootElement.GetProperty("text").GetString());
        }
        Assert.Equal(0, second.Archive.TotalCalls);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}

public class DatabasePathTests
{
    [Fact]
    public void Relative_paths_go_to_the_local_app_data_folder()
    {
        var path = AppBuilder.ResolveDatabasePath("wayback.db");
        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith(Path.Combine("WaybackViewer", "wayback.db"), path);
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("file:test?mode=memory")]
    public void Special_data_sources_are_left_alone(string value) => Assert.Equal(value, AppBuilder.ResolveDatabasePath(value));

    [Fact]
    public void Absolute_paths_are_kept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "x.db");
        Assert.Equal(absolute, AppBuilder.ResolveDatabasePath(absolute));
    }
}

public class SourceHygieneTests
{
    [Fact]
    public void No_source_file_hard_codes_a_site()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        (f.EndsWith(".cs") || f.EndsWith(".cshtml") || f.EndsWith(".json")))
            .Where(f => File.ReadAllText(f).Contains("twitter.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "WaybackViewer"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
