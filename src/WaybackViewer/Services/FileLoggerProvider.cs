using System.Text;
using System.Threading.Channels;

namespace WaybackViewer.Services;

/// Plain-text log files (one per day, rolled at MaxFileBytes), written by one background task from a bounded queue so
/// logging never blocks a request. When the queue is full the newest lines are dropped and counted.
/// Old files are deleted after RetentionDays. Line format: `2026-05-01T12:00:00.000Z INF [runId] Category message`.
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string directory;
    private readonly long maxFileBytes;
    private readonly int retentionDays;
    private readonly LogLevel minLevel;
    private readonly Channel<string> queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task writer;
    private long dropped;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information,
        long maxFileBytes = 50 * 1024 * 1024, int retentionDays = 7)
    {
        this.directory = directory;
        this.minLevel = minLevel;
        this.maxFileBytes = maxFileBytes;
        this.retentionDays = retentionDays;
        Directory.CreateDirectory(directory);
        writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        queue.Writer.TryComplete();
        writer.Wait(TimeSpan.FromSeconds(5));   // flush on shutdown (not a request path)
    }

    internal bool Enabled(LogLevel level) => level >= minLevel && level != LogLevel.None;

    internal void Enqueue(string line)
    {
        if (!queue.Writer.TryWrite(line)) Interlocked.Increment(ref dropped);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            DeleteOldFiles();
            StreamWriter? file = null;
            string? currentDay = null;
            var part = 0;
            try
            {
                await foreach (var line in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
                    if (file is null || day != currentDay || file.BaseStream.Length > maxFileBytes)
                    {
                        if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
                        if (day != currentDay) { currentDay = day; part = 0; } else part++;
                        var name = part == 0 ? $"wayback-{day}.log" : $"wayback-{day}.{part}.log";
                        file = new StreamWriter(new FileStream(Path.Combine(directory, name), FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = false };
                    }
                    await file.WriteLineAsync(line).ConfigureAwait(false);
                    if (queue.Reader.Count == 0)
                    {
                        var lost = Interlocked.Exchange(ref dropped, 0);
                        if (lost > 0) await file.WriteLineAsync($"{Stamp()} WRN FileLogger dropped {lost} log lines (queue full)").ConfigureAwait(false);
                        await file.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The log file itself is broken (disk full, locked): say so on stderr and stop; the app keeps running.
            Console.Error.WriteLine($"File logging stopped: {ex.GetType().Name}: {ex.Message}");
            queue.Writer.TryComplete();
        }
    }

    private void DeleteOldFiles()
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var path in Directory.EnumerateFiles(directory, "wayback-*.log"))
            if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
    }

    internal static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.Enabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT"
            };
            var sb = new StringBuilder(Stamp()).Append(' ').Append(level).Append(' ')
                .Append('[').Append(RunInfo.Id).Append("] ").Append(category).Append(' ').Append(formatter(state, exception).ReplaceLineEndings(" "));
            if (exception is not null) sb.Append(" | ").Append(exception.ToString().ReplaceLineEndings(" "));
            owner.Enqueue(sb.ToString());
        }
    }
}
