using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [Theory]
    [InlineData("https://web.archive.org/web/20200101000000id_/https://Example.com/a/b?x=1", "example.com")]
    [InlineData("https://web.archive.org/web/20200101000000/http://blog.example.org:80/p", "blog.example.org")]
    [InlineData("https://web.archive.org/cdx/search/cdx?url=example.com%2F&matchType=prefix", "cdx")]
    [InlineData("https://example.com/", "unknown")]
    public void Archive_log_host_is_extracted_without_the_full_url(string url, string expected)
    {
        Assert.Equal(expected, ArchiveClient.OriginalHost(url));
    }

    [Fact]
    public async Task Event_stream_requests_are_not_logged_as_slow()
    {
        var provider = new CapturingLoggerProvider();
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        var middleware = new RequestTimingMiddleware(async ctx =>
        {
            ctx.Response.ContentType = ctx.Request.Path == "/sse" ? "text/event-stream" : "text/plain";
            await Task.Delay(30);
        }, provider.CreateLogger<RequestTimingMiddleware>(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Performance:SlowRequestMs"] = "1" }).Build());

        var sse = new Microsoft.AspNetCore.Http.DefaultHttpContext(); sse.Request.Path = "/sse";
        var normal = new Microsoft.AspNetCore.Http.DefaultHttpContext(); normal.Request.Path = "/normal";
        await middleware.InvokeAsync(sse);
        await middleware.InvokeAsync(normal);

        Assert.Single(provider.Messages);
        Assert.Contains("/normal", provider.Messages[0]);
    }

    [Fact]
    public async Task Client_and_server_errors_are_logged_with_status_and_trace_id()
    {
        var provider = new CapturingLoggerProvider();
        var middleware = new RequestTimingMiddleware(ctx =>
        {
            ctx.Response.StatusCode = ctx.Request.Path == "/boom" ? 500 : 404;
            return Task.CompletedTask;
        }, provider.CreateLogger<RequestTimingMiddleware>(), new ConfigurationBuilder().Build());

        var notFound = new Microsoft.AspNetCore.Http.DefaultHttpContext { TraceIdentifier = "t-404" }; notFound.Request.Path = "/missing";
        var boom = new Microsoft.AspNetCore.Http.DefaultHttpContext { TraceIdentifier = "t-500" }; boom.Request.Path = "/boom";
        await middleware.InvokeAsync(notFound);
        await middleware.InvokeAsync(boom);

        Assert.Contains(provider.Messages, m => m.Contains("request.error") && m.Contains("status=404") && m.Contains("traceId=t-404"));
        Assert.Contains(provider.Messages, m => m.Contains("request.error") && m.Contains("status=500") && m.Contains("traceId=t-500"));
    }
    [Fact]
    public async Task Runtime_metrics_logger_is_registered_as_a_hosted_service()
    {
        await using var host = await AppHost.StartAsync();
        var hosted = host.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>();
        Assert.Contains(hosted, s => s is RuntimeMetricsLogger);
    }
    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public List<string> Messages { get; } = new();
        public Microsoft.Extensions.Logging.ILogger<T> CreateLogger<T>() => new Capture<T>(Messages);
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture<object>(Messages);
        public void Dispose() { }

        private sealed class Capture<T>(List<string> sink) : Microsoft.Extensions.Logging.ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Add(formatter(state, exception));
        }
    }}
