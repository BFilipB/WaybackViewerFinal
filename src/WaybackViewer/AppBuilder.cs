using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
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
        services.AddSingleton<IArchiveStoreProvider, SqliteArchiveStoreProvider>();
        services.AddSingleton<IArchiveStoreProvider, InMemoryArchiveStoreProvider>();
        services.AddSingleton<IArchiveStore>(sp => CreateStore(sp, builder.Configuration));
        services.AddSingleton<IArchiveClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArchiveOptions>>();
            return new ArchiveClient(ArchiveClient.CreateHttpClient(options.Value), options, sp.GetRequiredService<ITimeSource>());
        });
        services.AddSingleton<IPageLoader, PageLoader>();
        services.AddSingleton<CaptureCatalog>();
        services.AddSingleton<PageService>();
        services.AddSingleton<FacetCache>();
        services.AddSingleton<PreloadCoordinator>();
        services.AddTransient<LegacyCacheImporter>();
        services.AddHostedService<StoreInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<PreloadCoordinator>());

        services.AddRateLimiter(o =>
        {
            var permitLimit = builder.Configuration.GetValue<int?>("RateLimit:PermitLimit") ?? 1200;
            var windowSeconds = builder.Configuration.GetValue<int?>("RateLimit:WindowSeconds") ?? 60;
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "local",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, permitLimit),
                        Window = TimeSpan.FromSeconds(Math.Max(1, windowSeconds)),
                        QueueLimit = 0
                    }));
        });

        configureServices?.Invoke(services);

        var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/healthz", async (IArchiveStore store) =>
        {
            try { await store.GetOperationsAsync(true); return Results.Ok(new { status = "ok" }); }
            catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapDefaultControllerRoute();
        return app;
    }

    private static IArchiveStore CreateStore(IServiceProvider sp, IConfiguration configuration)
    {
        var provider = sp.GetRequiredService<IOptions<StorageOptions>>().Value.Provider;
        var providers = sp.GetServices<IArchiveStoreProvider>().ToList();
        var match = providers.LastOrDefault(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Unknown Storage:Provider '{provider}'. Available: {string.Join(", ", providers.Select(p => p.Name))}.");
        return match.Create(configuration);
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
