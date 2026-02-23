using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StayRate.Application.Bookings.ConfirmBooking;
using StayRate.Domain.Common;

namespace StayRate.Api.Middleware;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            IdempotencyConflictException => StatusCodes.Status409Conflict,
            DomainRuleViolation => StatusCodes.Status422UnprocessableEntity,
            ArgumentException => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        if (statusCode >= 500)
            _logger.LogError(exception, "Unhandled exception");
        else
            _logger.LogWarning(exception, "Handled exception");

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = GetTitle(statusCode),
            Detail = exception.Message,
            Type = $"https://httpstatuses.com/{statusCode}",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        if (exception is IdempotencyConflictException idempotencyConflictException)
            problem.Extensions["idempotencyKey"] = idempotencyConflictException.IdempotencyKey;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem
        });
    }

    private static string GetTitle(int statusCode)
    {
        return statusCode switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status409Conflict => "Conflict",
            StatusCodes.Status422UnprocessableEntity => "Unprocessable Entity",
            _ => "Internal Server Error"
        };
    }
}
