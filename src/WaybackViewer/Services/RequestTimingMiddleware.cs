using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WaybackViewer.Services;

/// Adds a Server-Timing header (total request time) and logs requests slower than the configured threshold.
public sealed class RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> log, IConfiguration config)
{
    private readonly long slowMs = Math.Max(1, config.GetValue("Performance:SlowRequestMs", 1000));

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var extra = context.Items[RequestTimingFilter.Key] as string;
            context.Response.Headers["Server-Timing"] = "total;dur=" + ms.ToString("0.0", CultureInfo.InvariantCulture) + extra;
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var status = context.Response.StatusCode;
            if (elapsed.TotalMilliseconds >= slowMs && !IsLongLived(context))
                log.LogWarning("Slow request {Method} {Path} took {Elapsed} ms (status {Status}).", context.Request.Method,
                    context.Request.Path, (long)elapsed.TotalMilliseconds, status);
            if (!IsLongLived(context) && context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is { } action)
            {
                var total = elapsed.TotalMilliseconds;
                log.Log(total >= 300 ? LogLevel.Information : LogLevel.Debug,
                    "request.done endpoint={Controller}.{Action} method={Method} status={Status} ms={Ms:F0} actionMs={ActionMs:F0} renderMs={RenderMs:F0} bytes={Bytes}",
                    action.ControllerName, action.ActionName, context.Request.Method, status, total,
                    context.Items[RequestTimingFilter.ActionMsKey] as double? ?? 0, context.Items[RequestTimingFilter.RenderMsKey] as double? ?? 0,
                    context.Response.ContentLength);
            }
            if (status >= 400 && status != StatusCodes.Status499ClientClosedRequest)
                log.Log(status >= 500 ? LogLevel.Error : LogLevel.Information,
                    "request.error method={Method} path={Path} status={Status} ms={Ms:F0} traceId={TraceId}",
                    context.Request.Method, context.Request.Path, status, elapsed.TotalMilliseconds, context.TraceIdentifier);
        }
    }

    /// Server-Sent Event streams stay open by design; their duration is not a latency problem.
    private static bool IsLongLived(HttpContext context) =>
        context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
}

/// MVC filter measuring action (controller + store/db work) and render (view/result execution) time for Server-Timing.
public sealed class RequestTimingFilter : IAsyncActionFilter, IAsyncResultFilter
{
    public const string Key = "wb.timing";
    private const string ActionKey = "wb.timing.action";

    public async Task OnActionExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        var t = Stopwatch.GetTimestamp();
        try { await next(); }
        finally { Append(context.HttpContext, "action", Stopwatch.GetElapsedTime(t).TotalMilliseconds); }
    }

    public async Task OnResultExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ResultExecutionDelegate next)
    {
        var t = Stopwatch.GetTimestamp();
        try { await next(); }
        finally { Append(context.HttpContext, "render", Stopwatch.GetElapsedTime(t).TotalMilliseconds); }
    }

    public const string ActionMsKey = "wb.timing.actionMs";
    public const string RenderMsKey = "wb.timing.renderMs";

    private static void Append(HttpContext ctx, string name, double ms)
    {
        ctx.Items[Key] = (ctx.Items[Key] as string ?? "") + ", " + name + ";dur=" + ms.ToString("0.0", CultureInfo.InvariantCulture);
        ctx.Items[name == "action" ? ActionMsKey : RenderMsKey] = ms;
    }
}
