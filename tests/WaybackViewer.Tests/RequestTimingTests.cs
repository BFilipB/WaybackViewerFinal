using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class RequestTimingTests
{
    [Fact]
    public async Task Responses_carry_a_server_timing_header()
    {
        await using var host = await AppHost.StartAsync();
        var response = await host.Client.GetAsync("/healthz");
        Assert.True(response.Headers.TryGetValues("Server-Timing", out var values));
        Assert.StartsWith("total;dur=", values!.Single());
    }

    [Fact]
    public void Middleware_type_is_public_for_pipeline_use()
    {
        Assert.True(typeof(RequestTimingMiddleware).IsPublic);
    }
}
