namespace Manuals.Logging;

using Microsoft.AspNetCore.Http;
using Serilog.Events;

internal static class RequestLogLevel
{
    public static LogEventLevel Of(HttpContext httpContext, double elapsedMilliseconds, Exception? exception)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return LogEventLevel.Warning;
        }

        return exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : LogEventLevel.Information;
    }
}
