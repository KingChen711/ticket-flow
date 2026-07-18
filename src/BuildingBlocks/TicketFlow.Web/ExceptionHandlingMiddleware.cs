using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Web;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // ArgumentException/InvalidOperationException are how the domain and application
            // layers currently signal expected business-rule failures (validation, invariants,
            // "not found"). Phase 1 simplification: scope.md's cross-cutting "Result pattern"
            // (mục 9) is the intended long-term approach instead of throwing for control flow —
            // revisit this mapping if/when that migration happens.
            var (statusCode, isExpected) = ex switch
            {
                ArgumentException or InvalidOperationException => (HttpStatusCode.BadRequest, true),
                _ => (HttpStatusCode.InternalServerError, false)
            };

            if (isExpected)
                logger.LogWarning(ex, "Request failed for {Method} {Path}: {Message}", context.Request.Method, context.Request.Path, ex.Message);
            else
                logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.ContentType = "application/problem+json";
            context.Response.StatusCode = (int)statusCode;

            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                title = isExpected ? ex.Message : "An unexpected error occurred.",
                status = context.Response.StatusCode,
                // Phase 10 Commit 1 — was context.TraceIdentifier (Kestrel's own per-connection
                // id, e.g. "0HN..."), which has no relationship to the OpenTelemetry trace this
                // request belongs to. Activity.Current.TraceId is the actual W3C trace id
                // (AddAspNetCoreInstrumentation starts one per request), so the id a caller sees
                // in an error response is the same one they can paste into Jaeger. Falls back to
                // TraceIdentifier only if OTel isn't wired for some reason (Activity.Current null).
                traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
            });
        }
    }
}
