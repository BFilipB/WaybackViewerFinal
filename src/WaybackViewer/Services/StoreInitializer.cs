using System.Diagnostics;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Creates the database/tables before the site starts serving requests (fails fast with a clear error).
/// Also writes the startup facts (run id, store, drives, extractor version) that every log analysis starts from.
public sealed class StoreInitializer(IArchiveStore store, ILogger<StoreInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("startup runId={RunId} os={Os} cpus={Cpus} store={Store} extractorVersion={Extractor} pid={Pid}",
            RunInfo.Id, Environment.OSVersion, Environment.ProcessorCount, store.GetType().Name,
            ExtractorVersion.Current, Environment.ProcessId);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            log.LogInformation("startup.drive name={Name} freeGb={Free:F1} totalGb={Total:F1}", d.Name, d.AvailableFreeSpace / 1e9, d.TotalSize / 1e9);

        if (store is SqliteArchiveStore sqlite) sqlite.Log = log;
        HtmlFileStore.Log ??= log;
        var started = Stopwatch.GetTimestamp();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        log.LogInformation("startup.store_initialized ms={Ms:F0}", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
