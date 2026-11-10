using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Right after the app starts, loads the searches you used most recently into memory (capture list, URL kinds,
/// filter counts) in the background. The first page you open is then as fast as every later one, and starting the
/// app is never delayed by it.
public sealed class WarmupService(IArchiveStore store, FacetCache facetCache, ILogger<WarmupService> log) : BackgroundService
{
    private const int SearchesToWarm = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();   // return control to the host at once; everything below runs in the background
        try { await WarmAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning(ex, "warmup.failed errorType={Type}", ex.GetType().Name); }   // purely an optimisation: never a reason to fail
    }

    /// Public so tests can run it directly.
    public async Task WarmAsync(CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var warmed = 0;
        var operations = await store.GetOperationsAsync(false, ct);
        var recent = operations
            .OrderByDescending(o => o.UpdatedAt)
            .Select(o => (o.Prefix, o.Filter))
            .Distinct()
            .Take(SearchesToWarm);

        foreach (var (prefix, filter) in recent)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await store.GetSearchAsync(prefix, filter, ct);
            if (snapshot is not null) { facetCache.GetOrBuild(snapshot); warmed++; }
        }
        log.LogInformation("warmup.done searches={Count} ms={Ms:F0}", warmed, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
