using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CrossRagfair.Hub;

internal sealed class HubRequestLoggingMiddleware(RequestDelegate next, ILogger<HubRequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var statusCode = failure is null
                ? context.Response.StatusCode
                : StatusCodes.Status500InternalServerError;
            logger.LogInformation("[客户端请求] {Method} {Path} -> {StatusCode} ({ElapsedMilliseconds:F1} ms)",
                context.Request.Method, context.Request.Path.Value ?? "/", statusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
