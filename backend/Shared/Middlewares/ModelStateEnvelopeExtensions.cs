using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Middlewares;

public static class ModelStateEnvelopeExtensions
{
    /// <summary>
    /// Makes automatic model-state validation answer in the same Envelope as everything
    /// else. Without this, [ApiController] short-circuits before the handler runs and
    /// returns RFC 9110 ProblemDetails - a third response shape alongside the success
    /// envelope and the one the exception middleware produces, on a route the client
    /// cannot distinguish in advance.
    /// </summary>
    public static IServiceCollection AddEnvelopeModelStateValidation(this IServiceCollection services)
    {
        return services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var messages = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .SelectMany(entry => entry.Value!.Errors.Select(error => new ErrorMessage(
                        "value.is.invalid",
                        string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Invalid value" : error.ErrorMessage,
                        entry.Key)))
                    .ToArray();

                return new ObjectResult(Envelope.Fail(Error.Validation(messages)))
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                };
            };
        });
    }
}
