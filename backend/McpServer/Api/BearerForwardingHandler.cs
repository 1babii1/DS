using Microsoft.Net.Http.Headers;

namespace McpServer.Api;

/// <summary>
/// Copies the caller's own bearer token onto every outgoing service call, so DirectoryService and
/// EmployeeService authorize the request exactly as if the caller had made it themselves.
/// <para>
/// Fails closed: with no token on the incoming request there is nothing to forward, and the call
/// is refused here rather than sent anonymously or under any other identity. McpServer has no
/// service credential of its own on purpose - that is what would let a tool read more than the
/// person using it is allowed to.
/// </para>
/// </summary>
public sealed class BearerForwardingHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var incoming = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();

        if (string.IsNullOrWhiteSpace(incoming))
        {
            throw ServiceApiException.From(System.Net.HttpStatusCode.Unauthorized);
        }

        request.Headers.Remove(HeaderNames.Authorization);
        request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, incoming);
        return base.SendAsync(request, cancellationToken);
    }
}
