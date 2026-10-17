using WaybackViewer.Models;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class OperationStoreTests
{
    [Fact]
    public async Task Active_operation_is_reused_for_duplicate_requests()
    {
        var store = new InMemoryArchiveStore();
        var first = await store.CreateOrGetOperationAsync("example.com/", "html200", OperationKind.WaybackRefresh, DateTimeOffset.UtcNow);
        var second = await store.CreateOrGetOperationAsync("example.com/", "html200", OperationKind.WaybackRefresh, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(OperationState.Queued, second.State);
    }

    [Fact]
    public async Task Stopped_operation_can_be_created_again()
    {
        var store = new InMemoryArchiveStore();
        var first = await store.CreateOrGetOperationAsync("example.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(first with { State = OperationState.Stopped, UpdatedAt = DateTimeOffset.UtcNow });

        var next = await store.CreateOrGetOperationAsync("example.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(OperationState.Queued, next.State);
    }

    [Fact]
    public async Task Operation_status_preserves_progress_and_error()
    {
        var store = new InMemoryArchiveStore();
        var operation = await store.CreateOrGetOperationAsync("example.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        var updated = operation with
        {
            State = OperationState.Failed,
            Total = 10,
            Completed = 8,
            Failed = 2,
            Error = "Wayback unavailable",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await store.UpdateOperationAsync(updated);

        var result = await store.GetOperationAsync(operation.Id);

        Assert.Equal(updated, result);
    }

    [Fact]
    public async Task Different_urls_have_independent_operations()
    {
        var store = new InMemoryArchiveStore();
        var first = await store.CreateOrGetOperationAsync("example.com/a/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        var second = await store.CreateOrGetOperationAsync("example.com/b/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);

        await store.UpdateOperationAsync(first with { State = OperationState.Paused, UpdatedAt = DateTimeOffset.UtcNow });

        var operations = await store.GetOperationsAsync(activeOnly: true);
        var firstStatus = operations.Single(operation => operation.Id == first.Id);
        var secondStatus = operations.Single(operation => operation.Id == second.Id);

        Assert.Equal(OperationState.Paused, firstStatus.State);
        Assert.Equal(OperationState.Queued, secondStatus.State);
    }
}
