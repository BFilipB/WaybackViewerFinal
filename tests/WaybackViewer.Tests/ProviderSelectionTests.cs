using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class ProviderSelectionTests
{
    [Fact]
    public async Task Memory_provider_can_be_selected_by_name()
    {
        await using var host = await AppHost.StartAsync(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory"
        });

        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        Assert.IsType<InMemoryArchiveStore>(store);
    }

    [Fact]
    public async Task Sqlite_provider_can_be_selected_by_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-provider-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await AppHost.StartAsync(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Sqlite",
                ["ConnectionStrings:WaybackDb"] = $"Data Source={Path.Combine(dir, "test.db")}" 
            });

            var store = host.App.Services.GetRequiredService<IArchiveStore>();
            Assert.IsType<SqliteArchiveStore>(store);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Unknown_provider_throws_with_available_names()
    {
        var app = AppBuilder.Build(Array.Empty<string>(), null, new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Nope"
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            app.Services.GetRequiredService<IArchiveStore>());

        Assert.Contains("Unknown Storage:Provider", ex.Message);
        Assert.Contains("Sqlite", ex.Message);
        Assert.Contains("Memory", ex.Message);
    }
}
