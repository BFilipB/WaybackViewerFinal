using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using WaybackViewer.Controllers;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer;

public static class AppBuilder
{
    /// Builds the web app. Tests pass `configureServices` to replace the archive client / store and `overrides`
    /// to change configuration (e.g. Storage:Provider=Memory).
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null,
        IEnumerable<KeyValuePair<string, string?>>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);

        var services = builder.Services;
        services.Configure<ArchiveOptions>(builder.Configuration.GetSection("Archive"));
        services.Configure<PreloadOptions>(builder.Configuration.GetSection("Preload"));
        services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

        services.AddControllersWithViews().AddApplicationPart(typeof(HomeController).Assembly);

        services.AddSingleton<ITimeSource, SystemTimeSource>();
        services.AddSingleton<IArchiveStore>(sp => CreateStore(sp, builder.Configuration));
        services.AddSingleton<IArchiveClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArchiveOptions>>();
            return new ArchiveClient(ArchiveClient.CreateHttpClient(options.Value), options, sp.GetRequiredService<ITimeSource>());
        });
        services.AddSingleton<IPageLoader, PageLoader>();
        services.AddSingleton<CaptureCatalog>();
        services.AddSingleton<PageService>();
        services.AddSingleton<PreloadCoordinator>();
        services.AddTransient<LegacyCacheImporter>();
        services.AddHostedService<StoreInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<PreloadCoordinator>());

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.UseRouting();
        app.MapDefaultControllerRoute();
        return app;
    }

    private static IArchiveStore CreateStore(IServiceProvider sp, IConfiguration configuration)
    {
        var provider = sp.GetRequiredService<IOptions<StorageOptions>>().Value.Provider;
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
            return new InMemoryArchiveStore();

        var connectionString = configuration.GetConnectionString("WaybackDb");
        if (string.IsNullOrWhiteSpace(connectionString)) connectionString = "Data Source=wayback.db";

        var builder = new SqliteConnectionStringBuilder(connectionString);
        builder.DataSource = ResolveDatabasePath(builder.DataSource);
        Console.WriteLine($"Database file: {builder.DataSource}");
        return new SqliteArchiveStore(builder.ToString());
    }

    /// A relative path is placed in the user's local application-data folder, so the database is found again no
    /// matter which folder the app is started from (and never ends up inside the git repository).
    public static string ResolveDatabasePath(string dataSource)
    {
        var expanded = Environment.ExpandEnvironmentVariables(dataSource);
        if (expanded == ":memory:" || expanded.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(expanded))
            return expanded;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = AppContext.BaseDirectory;
        return Path.Combine(root, "WaybackViewer", expanded);
    }
}
