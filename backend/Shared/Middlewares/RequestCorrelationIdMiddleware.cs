using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace Shared.Middlewares;

public class RequestCorrelationIdMiddleware
{
    private const string CORRELATION_ID_HEADER_NAME = "X-Correlation-Id";
    private const string CORRELATION_ID = "CorrelationId";
    private const int MAX_CORRELATION_ID_LENGTH = 128;

    private readonly RequestDelegate _next;

    public RequestCorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext context)
    {
        context.Request.Headers.TryGetValue(CORRELATION_ID_HEADER_NAME, out StringValues correlationIdValues);

        string correlationId = IsValidClientCorrelationId(correlationIdValues.FirstOrDefault())
            ? correlationIdValues.FirstOrDefault()!
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Response.Headers[CORRELATION_ID_HEADER_NAME] = correlationId;

        using (LogContext.PushProperty(CORRELATION_ID, correlationId))
        {
            return _next(context);
        }
    }

    // A client-supplied id is only trusted if it's short and made of characters that can't
    // break or pollute log lines — an unbounded or control-character-laden value from an
    // untrusted client would otherwise flow straight into every log line for the request
    // (BACKEND_AUDIT.md M12).
    private static bool IsValidClientCorrelationId(string? candidate) =>
        candidate is { Length: > 0 and <= MAX_CORRELATION_ID_LENGTH }
        && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public static class RequestCorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RequestCorrelationIdMiddleware>();
    }
}