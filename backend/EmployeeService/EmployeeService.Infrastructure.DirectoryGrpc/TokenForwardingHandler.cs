using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace EmployeeService.Infrastructure.DirectoryGrpc;

/// <summary>
/// Copies the caller's bearer token onto the outgoing gRPC call so DirectoryService can
/// authorize it. Every lookup this client makes happens while serving a user request that
/// already carried a token, so forwarding it keeps the original identity end to end -
/// no ambient service account that would let EmployeeService read more than its caller can.
/// </summary>
public sealed class TokenForwardingHandler(
    IHttpContextAccessor httpContextAccessor,
    ILogger<TokenForwardingHandler> logger) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var incoming = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();

        if (!string.IsNullOrWhiteSpace(incoming))
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, incoming);
            logger.LogDebug("Forwarded caller token to {Uri}", request.RequestUri);
        }
        else
        {
            logger.LogWarning(
                "No caller token to forward to {Uri}; HttpContext present: {HasContext}",
                request.RequestUri,
                httpContextAccessor.HttpContext is not null);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
