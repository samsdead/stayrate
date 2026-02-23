using System.Diagnostics;
using Microsoft.Extensions.Primitives;

namespace StayRate.Api.Middleware;

public sealed class RequestContextLoggingMiddleware(ILogger<RequestContextLoggingMiddleware> logger)
{
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string IdempotencyHeader = "Idempotency-Key";
    private readonly ILogger<RequestContextLoggingMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationHeader] = correlationId;

        var idempotencyKey = ResolveHeader(context, IdempotencyHeader);

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["IdempotencyKey"] = idempotencyKey
        }))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await next(context);
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        var headerValue = ResolveHeader(context, CorrelationHeader);
        return string.IsNullOrWhiteSpace(headerValue) ? context.TraceIdentifier : headerValue;
    }

    private static string? ResolveHeader(HttpContext context, string headerName)
    {
        return context.Request.Headers.TryGetValue(headerName, out StringValues value) &&
               !StringValues.IsNullOrEmpty(value)
            ? value.ToString()
            : null;
    }
}
