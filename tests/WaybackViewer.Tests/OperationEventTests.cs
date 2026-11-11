using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class OperationEventTests
{
    private static BackgroundOperation Op(long id, OperationState s, int done = 0) =>
        new(id, "a.com/", "", OperationKind.PagePreload, s, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10, done, 0);

    [Fact]
    public void Bus_assigns_increasing_ids_and_supports_catch_up()
    {
        var bus = new OperationEventBus();
        bus.Publish(Op(1, OperationState.Running, 1));
        bus.Publish(Op(1, OperationState.Running, 2));
        bus.Publish(Op(1, OperationState.Completed, 10));
        var missed = bus.Since(1);
        Assert.Equal(new long[] { 2, 3 }, missed.Select(e => e.Id));
        Assert.True(missed[1].Terminal);
        Assert.False(missed[0].Terminal);
    }

    [Fact]
    public void Bus_is_bounded()
    {
        var bus = new OperationEventBus(5);
        for (var i = 0; i < 20; i++) bus.Publish(Op(1, OperationState.Running, i));
        Assert.Equal(5, bus.Since(0, 100).Count);
        Assert.Equal(20, bus.LastId);
    }

    [Fact]
    public async Task Wait_completes_on_publish_and_honors_cancellation()
    {
        var bus = new OperationEventBus();
        var wait = bus.WaitAsync(0, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        bus.Publish(Op(1, OperationState.Running));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bus.WaitAsync(bus.LastId, cts.Token));
    }

    [Fact]
    public async Task Publisher_emits_on_change_only_and_a_terminal_event_when_finished()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var bus = new OperationEventBus();
        var publisher = new OperationEventPublisher(store, bus, NullLogger<OperationEventPublisher>.Instance) { MinProgressInterval = TimeSpan.Zero };
        var op = await store.CreateOrGetOperationAsync("evt.com/", "", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(op with { State = OperationState.Running, Total = 4, UpdatedAt = DateTimeOffset.UtcNow });

        await publisher.TickAsync(CancellationToken.None);
        var first = bus.LastId;
        await publisher.TickAsync(CancellationToken.None);
        Assert.Equal(first, bus.LastId);   // unchanged -> no event

        await store.UpdateOperationAsync(op with { State = OperationState.Running, Total = 4, Completed = 2, UpdatedAt = DateTimeOffset.UtcNow });
        await publisher.TickAsync(CancellationToken.None);
        Assert.Equal(first + 1, bus.LastId);

        await store.UpdateOperationAsync(op with { State = OperationState.Completed, Total = 4, Completed = 4, UpdatedAt = DateTimeOffset.UtcNow });
        await publisher.TickAsync(CancellationToken.None);
        var last = bus.Since(0).Last();
        Assert.True(last.Terminal);
        Assert.Equal("Completed", last.State);
    }

    [Fact]
    public async Task Publisher_throttles_progress_but_sends_state_changes_at_once()
    {
        await using var host = await AppHost.StartAsync();
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var bus = new OperationEventBus();
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var publisher = new OperationEventPublisher(store, bus, NullLogger<OperationEventPublisher>.Instance)
        { MinProgressInterval = TimeSpan.FromSeconds(1), Clock = () => now };
        var op = await store.CreateOrGetOperationAsync("throttle.com/", "", OperationKind.PagePreload, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(op with { State = OperationState.Running, Total = 100, UpdatedAt = DateTimeOffset.UtcNow });
        await publisher.TickAsync(CancellationToken.None);
        var first = bus.LastId;

        for (var i = 1; i <= 5; i++)   // five progress changes within the same second
        {
            await store.UpdateOperationAsync(op with { State = OperationState.Running, Total = 100, Completed = i, UpdatedAt = DateTimeOffset.UtcNow });
            await publisher.TickAsync(CancellationToken.None);
        }
        Assert.Equal(first, bus.LastId);

        now = now.AddSeconds(1);   // the latest progress goes out once the interval has passed
        await publisher.TickAsync(CancellationToken.None);
        Assert.Equal(first + 1, bus.LastId);
        Assert.Equal(5, bus.Since(first).Single().Completed);

        await store.UpdateOperationAsync(op with { State = OperationState.Paused, Total = 100, Completed = 6, UpdatedAt = DateTimeOffset.UtcNow });
        await publisher.TickAsync(CancellationToken.None);   // state change: not throttled
        Assert.Equal(first + 2, bus.LastId);
        Assert.Equal("Paused", bus.Since(first + 1).Single().State);
    }

    [Fact]
    public async Task Sse_endpoint_replays_missed_events_with_ids()
    {
        await using var host = await AppHost.StartAsync();
        var bus = host.App.Services.GetRequiredService<OperationEventBus>();
        bus.Publish(Op(1, OperationState.Running, 1));
        bus.Publish(Op(1, OperationState.Completed, 10));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/operations/events");
        request.Headers.Add("Last-Event-ID", "0");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var text = "";
        while (!text.Contains("\"terminal\":true"))
        {
            var line = await reader.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            text += line + "\n";
        }
        Assert.Contains("id: 1", text);
        Assert.Contains("id: 2", text);
        Assert.Contains("event: operation", text);
    }

    [Fact]
    public async Task Layout_has_opt_in_sound_toggle_and_live_script()
    {
        await using var host = await AppHost.StartAsync();
        var html = await host.Client.GetStringAsync("/about");
        Assert.Contains("id=\"soundToggle\"", html);
        Assert.Contains("/operations/events", html);
        Assert.Contains("wb-sound", html);
    }
}
