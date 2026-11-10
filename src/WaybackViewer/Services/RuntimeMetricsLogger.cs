using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace WaybackViewer.Services;

/// Writes one `runtime.metrics` line every interval (memory, GC, thread pool, CPU, free disk), a `preload.metrics`
/// line with throughput state, and drains per-host extraction coverage (`extract.summary`). Also writes one
/// `startup.config` line with the effective tuning options. Cheap: no allocation-heavy work, no I/O besides DriveInfo.
public sealed class RuntimeMetricsLogger(ILogger<RuntimeMetricsLogger> log, IConfiguration config,
    IServiceProvider services) : BackgroundService
{
    private readonly TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, config.GetValue("Logging:RuntimeMetricsSeconds", 30)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogConfig();
        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastAt = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                proc.Refresh();
                var cpu = proc.TotalProcessorTime;
                var wall = Stopwatch.GetElapsedTime(lastAt).TotalSeconds;
                var cpuPct = wall > 0 ? (cpu - lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100 : 0;
                lastCpu = cpu; lastAt = Stopwatch.GetTimestamp();
                log.LogInformation("runtime.metrics workingSetMb={Ws:F0} gcHeapMb={Heap:F0} gen0={G0} gen1={G1} gen2={G2} threads={Threads} poolQueue={Queue} poolThreads={Pool} cpuPct={Cpu:F1} freeGb={Free:F1} uptimeSec={Up:F0}",
                    proc.WorkingSet64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                    proc.Threads.Count, ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount, cpuPct, FreeGb(),
                    (DateTimeOffset.UtcNow - RunInfo.StartedAt).TotalSeconds);
                LogPreload();
                log.LogInformation("htmlstore.metrics saves={Saves} saveMb={Mb:F1} retries={Retries} loads={Loads} loadMisses={Misses}",
                    Interlocked.Read(ref WaybackViewer.Storage.HtmlFileStore.Saves), Interlocked.Read(ref WaybackViewer.Storage.HtmlFileStore.SaveBytes) / 1048576.0,
                    Interlocked.Read(ref WaybackViewer.Storage.HtmlFileStore.SaveRetries), Interlocked.Read(ref WaybackViewer.Storage.HtmlFileStore.Loads),
                    Interlocked.Read(ref WaybackViewer.Storage.HtmlFileStore.LoadMisses));
                ExtractionStats.Flush(log);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            ExtractionStats.Flush(log);   // keep the last interval's coverage on shutdown
        }
    }

    public void LogConfig()
    {
        var archive = (services.GetService(typeof(IOptions<ArchiveOptions>)) as IOptions<ArchiveOptions>)?.Value;
        var preload = (services.GetService(typeof(IOptions<PreloadOptions>)) as IOptions<PreloadOptions>)?.Value;
        var storage = (services.GetService(typeof(IOptions<StorageOptions>)) as IOptions<StorageOptions>)?.Value;
        log.LogInformation("startup.config archiveConcurrency={Conc} minIntervalMs={MinI} maxIntervalMs={MaxI} archiveAttempts={AA} timeoutSec={To} preloadScope={Scope} workers={Workers} writeBatch={WB} writeDelayMs={WD} maxHtmlChars={MaxHtml} preloadAttempts={PA} storage={Provider} searchMaxAgeDays={Age} slowRequestMs={Slow} metricsSec={Metrics}",
            archive?.MaxConcurrency, archive?.MinIntervalMs, archive?.MaxIntervalMs, archive?.MaxAttempts, archive?.RequestTimeoutSeconds,
            preload?.Scope, preload?.Workers, preload?.WriteBatchSize, preload?.WriteMaxDelayMs, preload?.MaxHtmlChars, preload?.MaxAttempts,
            storage?.Provider, storage?.SearchMaxAgeDays, config.GetValue("Performance:SlowRequestMs", 1000), interval.TotalSeconds);
    }

    private void LogPreload()
    {
        if (services.GetService(typeof(PreloadCoordinator)) is not PreloadCoordinator preload) return;
        var s = preload.GetStatus();
        if (!s.Running && s.Total == 0) return;   // idle: no line
        log.LogInformation("preload.metrics running={Running} total={Total} completed={Completed} failed={Failed} perSec={PerSec:F2} jobs={Jobs}",
            s.Running, s.Total, s.Completed, s.Failed, s.PerSecond, s.Jobs?.Count ?? 0);
    }

    private static double FreeGb()
    {
        try { return new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!).AvailableFreeSpace / 1e9; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return -1; }
    }
}
