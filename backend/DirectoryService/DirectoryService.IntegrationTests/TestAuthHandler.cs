using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DirectoryService.IntegrationTests;

/// <summary>
/// Every existing test in this project calls handlers directly, bypassing HTTP and
/// auth entirely - but proving an HTTP-level status/body contract (this task's whole
/// point) requires an actual authenticated request through the real ASP.NET Core
/// pipeline, including [Authorize]. Real JWT/JWKS validation needs a running
/// AuthService this test host doesn't have, so this replaces the default
/// authentication scheme with one that always succeeds as a fixed authenticated
/// principal - the same well-known pattern WebApplicationFactory tests use to
/// exercise [Authorize] without standing up a real identity provider.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("name", "test-user"),
            new Claim("role", Shared.Security.RoleNames.Admin),
        };
        var identity = new ClaimsIdentity(claims, SchemeName, "name", "role");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
