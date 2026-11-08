using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WaybackViewer.Models;
using WaybackViewer.Services;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class DeleteRunnerTests
{
    private const string A = "a.com/";
    private const string F = "html200";

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task SeedAsync(AppHost host)
    {
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        await store.SaveSearchAsync(A, F, SampleData.Captures(5), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Delete_all_runs_in_background_and_removes_the_url()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host);
        var runner = host.App.Services.GetRequiredService<DeleteRunner>();

        Assert.Equal(DeleteStartResult.Started, await runner.BeginAsync(A, F, DeleteLevel.All));
        await runner.WhenIdleAsync();

        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        Assert.Null(await store.GetSearchAsync(A, F));
        Assert.Empty(await store.GetOperationsForSearchAsync(A, F));
    }

    [Fact]
    public async Task Delete_pages_keeps_the_capture_list()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host);
        var runner = host.App.Services.GetRequiredService<DeleteRunner>();

        await runner.BeginAsync(A, F, DeleteLevel.Pages);
        await runner.WhenIdleAsync();

        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        Assert.Equal(5, (await store.GetSearchAsync(A, F))!.Captures.Count);
    }

    [Fact]
    public async Task Unfinished_delete_resumes_after_restart()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host);
        var store = host.App.Services.GetRequiredService<IArchiveStore>();
        var op = await store.CreateOrGetOperationAsync(A, F, OperationKind.DeleteAll, DateTimeOffset.UtcNow);
        await store.UpdateOperationAsync(op with { State = OperationState.Deleting, UpdatedAt = DateTimeOffset.UtcNow });

        var runner = host.App.Services.GetRequiredService<DeleteRunner>();
        await runner.StartAsync(CancellationToken.None);
        await runner.WhenIdleAsync();

        Assert.Null(await store.GetSearchAsync(A, F));
    }

    [Fact]
    public async Task Delete_endpoints_reject_missing_token_and_bad_input()
    {
        await using var host = await AppHost.StartAsync();
        using var raw = new HttpClient { BaseAddress = host.Client.BaseAddress };
        var noToken = await raw.PostAsync("/operations/delete", Body(new { key = new { prefix = A, filter = F }, level = "all" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var badKey = await host.Client.PostAsync("/operations/delete", Body(new { key = new { prefix = "has space", filter = F }, level = "all" }));
        Assert.Equal(HttpStatusCode.BadRequest, badKey.StatusCode);

        var badLevel = await host.Client.PostAsync("/operations/delete", Body(new { key = new { prefix = A, filter = F }, level = "nope" }));
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
    }

    [Fact]
    public async Task Delete_endpoint_accepts_valid_request_and_preview_returns_counts()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host);

        var preview = await host.Client.PostAsync("/operations/delete-preview", Body(new { key = new { prefix = A, filter = F }, level = "all" }));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var doc = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(5, doc.RootElement.GetProperty("captures").GetInt32());

        var res = await host.Client.PostAsync("/operations/delete", Body(new { key = new { prefix = A, filter = F }, level = "all" }));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await host.App.Services.GetRequiredService<DeleteRunner>().WhenIdleAsync();
    }
}
