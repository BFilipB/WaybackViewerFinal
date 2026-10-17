namespace WaybackViewer.Services;

/// Clock + delay abstraction so pacing/backoff logic can be tested without real waiting.
public interface ITimeSource
{
    DateTime UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct = default);
}

public sealed class SystemTimeSource : ITimeSource
{
    public DateTime UtcNow => DateTime.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
}
