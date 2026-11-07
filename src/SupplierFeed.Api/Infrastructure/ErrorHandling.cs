using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace SupplierFeed.Api.Infrastructure;

/// <summary>
/// Maps unhandled exceptions to problem responses without leaking internals.
/// A busy or locked database (another instance holding the write lock past the busy
/// timeout) is a temporary condition, so it returns 503 rather than 500.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception for {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        var busy = exception is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked };
        var problem = new ProblemDetails
        {
            Status = busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
            Title = busy ? "The database is busy, try again shortly." : "An unexpected error occurred."
        };

        httpContext.Response.StatusCode = problem.Status.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
