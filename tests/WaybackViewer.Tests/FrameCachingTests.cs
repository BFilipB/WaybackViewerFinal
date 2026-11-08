using System.Net;
using WaybackViewer.Models;

namespace WaybackViewer.Tests;

public class FrameCachingTests
{
    [Fact]
    public async Task Frame_sends_an_etag_and_answers_revalidation_with_304_and_no_body()
    {
        await using var host = await AppHost.StartAsync();
        var c = SampleData.Capture(1);
        host.RouteSite("example.com/", [c]);

        var first = await host.Client.GetAsync(AppHost.FrameUrl(c));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, AppHost.FrameUrl(c));
        request.Headers.IfNoneMatch.Add(etag);
        var second = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());

        // A different tag must not be answered with 304.
        var other = new HttpRequestMessage(HttpMethod.Get, AppHost.FrameUrl(c));
        other.Headers.TryAddWithoutValidation("If-None-Match", "\"stale\"");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(other)).StatusCode);
    }
}
