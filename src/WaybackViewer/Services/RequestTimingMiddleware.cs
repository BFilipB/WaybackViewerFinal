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
            if (elapsed.TotalMilliseconds >= slowMs)
                log.LogWarning("Slow request {Method} {Path} took {Elapsed} ms (status {Status}).", context.Request.Method,
                    context.Request.Path, (long)elapsed.TotalMilliseconds, context.Response.StatusCode);
        }
    }
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

    private static void Append(HttpContext ctx, string name, double ms) =>
        ctx.Items[Key] = (ctx.Items[Key] as string ?? "") + ", " + name + ";dur=" + ms.ToString("0.0", CultureInfo.InvariantCulture);
}