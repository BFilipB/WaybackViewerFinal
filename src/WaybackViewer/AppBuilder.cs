using Microsoft.AspNetCore.DataProtection;
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

        if (builder.Configuration["Logging:File:Directory"] is { Length: > 0 } logDir)
            builder.Logging.AddProvider(new FileLoggerProvider(logDir,
                Enum.TryParse<LogLevel>(builder.Configuration["Logging:File:MinLevel"], out var lvl) ? lvl : LogLevel.Information));
        RedirectTempDirectory(builder.Configuration["Storage:TempDirectory"]);
        var services = builder.Services;
        services.Configure<ArchiveOptions>(builder.Configuration.GetSection("Archive"));
        services.Configure<PreloadOptions>(builder.Configuration.GetSection("Preload"));
        services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));

        services.AddMemoryCache(o => o.SizeLimit = 256);   // bounded: autocomplete results
    services.AddControllersWithViews(o => { o.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()); o.Filters.Add<WaybackViewer.Services.RequestTimingFilter>(); })
            .AddApplicationPart(typeof(HomeController).Assembly);
        // JS fetch calls send the token from the page's <meta name="request-verification-token"> in this header.
        if (builder.Configuration["Storage:KeysDirectory"] is { Length: > 0 } keysDir)
            services.AddDataProtection().PersistKeysToFileSystem(Directory.CreateDirectory(
                Path.IsPathRooted(keysDir) ? keysDir : ResolveDatabasePath(keysDir)));
        services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        services.AddResponseCompression(o =>
        {
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
            o.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
        });
        services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
        services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

        services.AddSingleton<ITimeSource, SystemTimeSource>();
        services.AddSingleton<IArchiveStoreProvider, SqliteArchiveStoreProvider>();
        services.AddSingleton<IArchiveStoreProvider, InMemoryArchiveStoreProvider>();
        services.AddSingleton<IArchiveStoreProvider, PostgresArchiveStoreProvider>();
        services.AddSingleton<IArchiveStore>(sp => CreateStore(sp, builder.Configuration));
        services.AddSingleton<IArchiveClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArchiveOptions>>();
            return new ArchiveClient(ArchiveClient.CreateHttpClient(options.Value), options, sp.GetRequiredService<ITimeSource>(), sp.GetService<ILogger<ArchiveClient>>());
        });
        services.AddSingleton<IPageLoader, PageLoader>();
        services.AddSingleton<CaptureCatalog>();
        services.AddSingleton<PageService>();
        services.AddSingleton<FacetCache>();
        services.AddSingleton<PreloadCoordinator>();
        services.AddTransient<LegacyCacheImporter>();
        services.AddHostedService<StoreInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<PreloadCoordinator>());
        services.AddSingleton<CaptureLoadRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<CaptureLoadRunner>());
        services.AddSingleton<OperationControl>();
        services.AddSingleton<DeleteRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<DeleteRunner>());
        services.AddSingleton<OperationEventBus>();
        services.AddHostedService<OperationEventPublisher>();
        services.AddHostedService<RuntimeMetricsLogger>();
        services.AddSingleton<WarmupService>();
        services.AddHostedService(sp => sp.GetRequiredService<WarmupService>());

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
        app.UseMiddleware<WaybackViewer.Services.RequestTimingMiddleware>();   // Server-Timing + slow-request log
app.UseResponseCompression();   // big list pages and the filtered-set JSON shrink 10-20x

		// Static assets: fingerprinted requests (?v=...) never change for a given URL, so cache them for a year;

		// everything else revalidates (ETag) so an edited file is picked up on the next load.

		app.UseStaticFiles(new StaticFileOptions

		{

			OnPrepareResponse = ctx =>

				ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Query.ContainsKey("v")

					? "public,max-age=31536000,immutable"

					: "public,max-age=0,must-revalidate"

		});
        app.UseRouting();
        app.UseRateLimiter();
        var SseJson = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        app.MapGet("/operations/events", async (HttpContext ctx, OperationEventBus bus, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var sseLog = loggers.CreateLogger("WaybackViewer.Sse");
            var sseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var sent = 0;
            var closeReason = "deadline";
            long after = 0;
            var header = ctx.Request.Headers["Last-Event-ID"].ToString();
            if (!long.TryParse(header, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out after))
                long.TryParse(ctx.Request.Query["after"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out after);
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            sseLog.LogInformation("sse.open after={After} reconnect={Reconnect}", after, header.Length > 0);
            await ctx.Response.WriteAsync("retry: 3000\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
            var deadline = DateTime.UtcNow.AddMinutes(5);   // client reconnects with Last-Event-ID
            try
            {
                while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    foreach (var ev in bus.Since(after))
                    {
                        await ctx.Response.WriteAsync($"id: {ev.Id}\nevent: operation\ndata: {System.Text.Json.JsonSerializer.Serialize(ev, SseJson)}\n\n", ct);
                        after = ev.Id;
                        sent++;
                    }
                    await ctx.Response.Body.FlushAsync(ct);
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tick.CancelAfter(TimeSpan.FromSeconds(15));
                    try { await bus.WaitAsync(after, tick.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": keep-alive\n\n", ct);   // heartbeat
                    }
                }
            }
            catch (OperationCanceledException) { closeReason = "client_disconnected"; }
            finally
            {
                sseLog.LogInformation("sse.close reason={Reason} events={Events} lastId={LastId} durationSec={Sec:F0}", closeReason, sent, after,
                    System.Diagnostics.Stopwatch.GetElapsedTime(sseStart).TotalSeconds);
            }
        });
        app.MapGet("/healthz", async (IArchiveStore store, ILoggerFactory loggers, CancellationToken ct) =>
        {
            try { await store.GetOperationsAsync(true, ct); return Results.Ok(new { status = "ok" }); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggers.CreateLogger("WaybackViewer.Health").LogError(ex, "health.failed errorType={Type}", ex.GetType().Name);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapDefaultControllerRoute();
        return app;
    }

    private static readonly string[] KnownExternalProviders = ["SqlServer", "MySql", "MariaDb", "Oracle"];

    private static IArchiveStore CreateStore(IServiceProvider sp, IConfiguration configuration)
    {
        var provider = sp.GetRequiredService<IOptions<StorageOptions>>().Value.Provider;
        var providers = sp.GetServices<IArchiveStoreProvider>().ToList();
        var match = providers.LastOrDefault(p => string.Equals(p.Name, provider, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.Create(configuration);

        var available = string.Join(", ", providers.Select(p => p.Name));
        if (KnownExternalProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"Storage:Provider '{provider}' is not bundled with this build (available: {available}). " +
                "Implement IArchiveStoreProvider for it and register it with AddSingleton<IArchiveStoreProvider, ...>(); " +
                "see docs/DATABASES.md.");
        throw new InvalidOperationException($"Unknown Storage:Provider '{provider}'. Available: {available}.");
    }

    /// Points TMP/TEMP of this process at dir (created if missing). SQLite and Path.GetTempPath read these,
    /// so temp files land on the data drive. Returns the applied folder, or null when not configured.
    public static string? RedirectTempDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var full = Path.GetFullPath(Path.IsPathRooted(dir) ? dir : ResolveDatabasePath(dir));
        Directory.CreateDirectory(full);
        Environment.SetEnvironmentVariable("TMP", full);
        Environment.SetEnvironmentVariable("TEMP", full);
        Environment.SetEnvironmentVariable("SQLITE_TMPDIR", full);
        return full;
    }

    /// The drive that holds all data. Override with the WAYBACKVIEWER_DATA_ROOT environment variable.
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("WAYBACKVIEWER_DATA_ROOT") is { Length: > 0 } custom ? custom : @"E:\";

    /// A relative path is placed under the data root (E:\ by default). There is deliberately no fallback to another
    /// drive: when the data root is unavailable this throws, so nothing is ever written to C:.
    public static string ResolveDatabasePath(string dataSource) => ResolveDatabasePath(dataSource, DataRoot);

    public static string ResolveDatabasePath(string dataSource, string dataRoot)
    {
        var expanded = Environment.ExpandEnvironmentVariables(dataSource);
        if (expanded == ":memory:" || expanded.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(expanded))
            return expanded;
        var driveRoot = Path.GetPathRoot(dataRoot);
        if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot))
            throw new InvalidOperationException(
                $"The data drive '{driveRoot ?? dataRoot}' is not available. Connect it and restart the app " +
                "(set WAYBACKVIEWER_DATA_ROOT to use a different drive).");
        return Path.Combine(dataRoot, "WaybackViewer", expanded);
    }
}
