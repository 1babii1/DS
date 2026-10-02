using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NotificationService.Web.HubTickets;

public static class HubTicketDefaults
{
    public const string Scheme = "HubTicket";
}

/// <summary>
/// Authenticates the SignalR handshake. The browser's WebSocket cannot set a header, so SignalR sends the credential
/// as the <c>access_token</c> query parameter; it is read here and nowhere else, and it must be a hub ticket. The
/// hub (and only the hub) is bound to this scheme, so the REST endpoints, which use the JWT bearer scheme, never look
/// at a query string.
/// </summary>
public sealed class HubTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HubTicketService tickets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? ticket = Request.Query["access_token"];
        if (string.IsNullOrEmpty(ticket))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (tickets.Validate(ticket) is not { } accountId)
        {
            return Task.FromResult(AuthenticateResult.Fail("The hub ticket is not valid."));
        }

        var identity = new ClaimsIdentity([new Claim("sub", accountId.ToString())], HubTicketDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), HubTicketDefaults.Scheme)));
    }
}
