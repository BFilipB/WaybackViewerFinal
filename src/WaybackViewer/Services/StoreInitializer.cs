using WaybackViewer.Storage;

namespace WaybackViewer.Services;

/// Creates the database/tables before the site starts serving requests (fails fast with a clear error).
public sealed class StoreInitializer(IArchiveStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
