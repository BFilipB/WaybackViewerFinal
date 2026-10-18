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
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
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
    public async Task Unusable_input_shows_a_message_without_calling_the_archive()
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
