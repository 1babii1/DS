using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Exceptions;

namespace Shared.Middlewares;

public class ExceptionMiddleware
{
    /// <summary>
    ///     Клиент закрыл соединение — не наш сбой. 499 (nginx-конвенция) вместо 500,
    ///     Information вместо Error, тела нет: писать некому.
    /// </summary>
    /// <remarks>Non-standard but widely used status for "client closed request".</remarks>
    private const int STATUS_CLIENT_CLOSED_REQUEST = 499;

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        // Отмена клиентом — не ошибка сервера. Мобильный пользователь ушёл со страницы,
        // и если считать это дефектом, получаем ложные алерты и испорченный SLO
        // при полностью здоровой системе.
        //
        // Client cancellation is not a server failure. A mobile user navigated away,
        // and treating that as a defect produces false alerts and a broken SLO
        // while the system is perfectly healthy.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Request canceled by client: {Method} {Path}",
                context.Request.Method,
                context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = STATUS_CLIENT_CLOSED_REQUEST;
            }

            return;
        }

        _logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", context.Request.Method, context.Request.Path);

        (int statusCode, Error error) = exception switch
        {
            // OperationCanceledException БЕЗ отмены запроса клиентом — это наш
            // внутренний таймаут, а не уход пользователя. Разные причины, разная реакция.
            // OperationCanceledException WITHOUT client abort is our own timeout,
            // not a user leaving. Different causes, different responses.
            OperationCanceledException => (
                StatusCodes.Status504GatewayTimeout,
                Error.Failure("request.timeout", "Превышено время ожидания")),

            NotFoundException ex => (StatusCodes.Status404NotFound, ex.Error),

            ValidationException ex => (StatusCodes.Status400BadRequest, ex.Error),

            ConflictException ex => (StatusCodes.Status409Conflict, ex.Error),

            FailureException ex => (StatusCodes.Status500InternalServerError, ex.Error),

            AuthenticationException => (StatusCodes.Status401Unauthorized, Error.Failure("authentication.failed", "Не удалось выполнить вход")),

            BadHttpRequestException => (StatusCodes.Status400BadRequest, Error.Validation("request.invalid", "Некорректный запрос")),

            _ => (StatusCodes.Status500InternalServerError, Error.Failure("server.internal", "Произошла непредвиденная ошибка"))
        };

        // Если ответ уже начал отправляться, менять статус поздно — попытка
        // приведёт к InvalidOperationException поверх исходной ошибки.
        // If the response has already started, changing the status is too late —
        // attempting it throws InvalidOperationException on top of the original error.
        if (context.Response.HasStarted)
        {
            return;
        }

        var envelope = Envelope.Fail(error);
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(envelope);
    }
}

public static class ExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionMiddleware(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ExceptionMiddleware>();
    }
}