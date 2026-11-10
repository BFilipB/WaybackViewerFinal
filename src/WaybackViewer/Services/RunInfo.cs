namespace WaybackViewer.Services;

/// One short id per process start. Written on every file log line so restarts, parallel instances and test hosts
/// that share a log folder can be told apart by the summarizer.
public static class RunInfo
{
    public static readonly string Id = Guid.NewGuid().ToString("N")[..8];
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
}