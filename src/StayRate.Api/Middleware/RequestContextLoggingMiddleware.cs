using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace StayRate.Api.Middleware;

public sealed class RequestContextLoggingMiddleware(RequestDelegate next, ILogger<RequestContextLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = context.TraceIdentifier
        });

        var start = Stopwatch.GetTimestamp();
        await next(context);
        logger.LogInformation(
            "HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs}ms",
            context.Request.Method,
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            context.Response.StatusCode,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
