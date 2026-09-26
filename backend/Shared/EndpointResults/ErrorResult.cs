using Microsoft.AspNetCore.Http;
using Shared;

namespace Shared.EndpointResults;

public class ErrorResult : IResult
{
    private readonly Error _error;

    public ErrorResult(Error error)
    {
        _error = error;
    }

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        int statusCode = GetStatusCodeFromErrorType(_error.Type);

        var envelope = Envelope.Fail(_error);
        httpContext.Response.StatusCode = statusCode;

        return httpContext.Response.WriteAsJsonAsync(envelope);
    }

    private static int GetStatusCodeFromErrorType(ErrorType errorType) =>
        errorType switch
        {
            ErrorType.VALIDATION => StatusCodes.Status400BadRequest,
            ErrorType.NOT_FOUND => StatusCodes.Status404NotFound,
            ErrorType.CONFLICT => StatusCodes.Status409Conflict,
            ErrorType.FAILURE => StatusCodes.Status500InternalServerError,
            ErrorType.AUTHENTICATION => StatusCodes.Status401Unauthorized,
            ErrorType.AUTHORIZATION => StatusCodes.Status403Forbidden,
            ErrorType.RATE_LIMITED => StatusCodes.Status429TooManyRequests,
            ErrorType.UNPROCESSABLE_ENTITY => StatusCodes.Status422UnprocessableEntity,
            ErrorType.UNAVAILABLE => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError
        };
}