using System.Text.Json;
using FluentValidation;
using Shared;

namespace DirectoryService.Middleware;

public class ExeptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExeptionHandlingMiddleware> _logger;

    public ExeptionHandlingMiddleware(RequestDelegate next, ILogger<ExeptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            _logger.LogInformation("Middleware started");
            await _next(context);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in middleware");
            await HandleExceptionAsync(context, e);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        _logger.LogError(exception, exception.Message);

        int code;
        Error[] errors;

        switch (exception)
        {
            case FluentValidation.ValidationException valEx:
                code = StatusCodes.Status400BadRequest;
                errors = valEx.Errors
                    .GroupBy(x => x.PropertyName)
                    .Select(g => Error.Failure(g.Key ?? null!, string.Join("; ", g.Select(x => x.ErrorMessage))))
                    .ToArray();
                break;

            case BadHttpRequestException badReq:
                code = StatusCodes.Status400BadRequest;
                errors = new[] { Error.Failure(null!, badReq.Message) };
                break;

            default:
                code = StatusCodes.Status500InternalServerError;
                errors = new[] { Error.Failure(null!, "something went wrong") };
                break;
        }

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = code;
        await context.Response.WriteAsJsonAsync(errors);
    }
}