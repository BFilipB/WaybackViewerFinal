using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

/// Proves the storage seam: another database is added by registering an IArchiveStoreProvider and setting
/// Storage:Provider, with no change to the rest of the application.
public class StorageProviderSeamTests
{
    private sealed class FakeDbProvider : IArchiveStoreProvider
    {
        public string? SeenConnectionString;
        public string Name => "FakeDb";
        public IArchiveStore Create(IConfiguration configuration)
        {
            SeenConnectionString = configuration.GetConnectionString("WaybackDb");
            return new InMemoryArchiveStore();
        }
    }

    [Fact]
    public async Task A_custom_provider_is_selected_by_name_and_receives_the_connection_string()
    {
        var provider = new FakeDbProvider();
        var app = AppBuilder.Build(["--urls", "http://127.0.0.1:0"],
            s => s.AddSingleton<IArchiveStoreProvider>(provider),
            new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "FakeDb",
                ["ConnectionStrings:WaybackDb"] = "Host=db;Database=x",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        await app.StartAsync();
        try
        {
            Assert.IsType<InMemoryArchiveStore>(app.Services.GetRequiredService<IArchiveStore>());
            Assert.Equal("Host=db;Database=x", provider.SeenConnectionString);
        }
        finally { await app.DisposeAsync(); }
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("MySql")]
    [InlineData("Oracle")]
    public async Task Known_but_unbundled_databases_fail_with_guidance(string name)
    {
        var app = AppBuilder.Build(["--urls", "http://127.0.0.1:0"], null,
            new Dictionary<string, string?> { ["Storage:Provider"] = name, ["Logging:LogLevel:Default"] = "Warning" });
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => app.Services.GetRequiredService<IArchiveStore>());
            Assert.Contains("DATABASES.md", ex.ToString());
            Assert.Contains("Sqlite", ex.ToString());
        }
        finally { await app.DisposeAsync(); }
    }

    [Fact]
    public async Task Unknown_provider_lists_available_ones()
    {
        var app = AppBuilder.Build(["--urls", "http://127.0.0.1:0"], null,
            new Dictionary<string, string?> { ["Storage:Provider"] = "Nope", ["Logging:LogLevel:Default"] = "Warning" });
        try
        {
            var ex = Assert.ThrowsAny<Exception>(() => app.Services.GetRequiredService<IArchiveStore>());
            Assert.Contains("Available:", ex.ToString());
            foreach (var name in new[] { "Sqlite", "Memory", "Postgres" }) Assert.Contains(name, ex.ToString());
        }
        finally { await app.DisposeAsync(); }
    }
}
