using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class ControlAndWarmupLoggingTests
{
    private sealed class ListLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public async Task Operation_control_logs_pause_resume_and_retry()
    {
        await using var host = await AppHost.StartAsync();
        var logs = new List<string>();
        var control = new OperationControl(host.App.Services.GetRequiredService<IArchiveStore>(),
            host.App.Services.GetRequiredService<PreloadCoordinator>(), host.App.Services.GetRequiredService<CaptureLoadRunner>(),
            new ListLogger<OperationControl>(logs));
        var key = new SearchKey("example.com/a/", "html200");

        await control.PauseAsync(key);
        await control.ResumeAsync(key);
        await control.RetryFailedAsync(key);

        Assert.Contains(logs, l => l.Contains("operation.control action=pause") && l.Contains("operations=0"));
        Assert.Contains(logs, l => l.Contains("operation.control action=resume"));
        Assert.Contains(logs, l => l.Contains("operation.control action=retry_failed") && l.Contains("pages=0"));
    }

    [Fact]
    public async Task Warmup_logs_completion_with_search_count()
    {
        await using var host = await AppHost.StartAsync();
        var logs = new List<string>();
        var warmup = new WarmupService(host.App.Services.GetRequiredService<IArchiveStore>(),
            host.App.Services.GetRequiredService<FacetCache>(), new ListLogger<WarmupService>(logs));

        await warmup.WarmAsync();

        Assert.Contains(logs, l => l.StartsWith("Information") && l.Contains("warmup.done searches="));
    }
}