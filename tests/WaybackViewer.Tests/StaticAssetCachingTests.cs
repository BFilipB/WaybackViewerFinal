using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class StaticAssetCachingTests
{
    [Fact]
    public async Task Fingerprinted_assets_are_cached_for_a_year_and_plain_ones_revalidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "wbv-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(root, "wwwroot", "site.css"), "body{}");

        var app = AppBuilder.Build(["--urls", "http://127.0.0.1:0", "--contentRoot", root, "--webroot", Path.Combine(root, "wwwroot")],
            null, new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Sqlite",
                ["ConnectionStrings:WaybackDb"] = $"Data Source={Path.Combine(root, "t.db")}",
                ["Storage:HtmlRoot"] = Path.Combine(root, "html-payloads"),
                ["Logging:File:Directory"] = "",
            });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            var versioned = await client.GetAsync("/site.css?v=abc123");
            Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
            Assert.Equal("public,max-age=31536000,immutable", versioned.Headers.CacheControl!.ToString().Replace(", ", ","));

            var plain = await client.GetAsync("/site.css");
            Assert.Contains("must-revalidate", plain.Headers.CacheControl!.ToString());
            Assert.NotNull(plain.Headers.ETag);
        }
        finally
        {
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
