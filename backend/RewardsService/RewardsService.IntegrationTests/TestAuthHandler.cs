using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RewardsService.IntegrationTests;

// Stands in for the real JWT validation so a test can act as a given role over real HTTP,
// with the real authorization policies still applied. No header means unauthenticated.
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";
    public const string RoleHeader = "X-Test-Role";
    public const string ElevatedHeader = "X-Test-Elevated";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoleHeader, out var role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new(ClaimTypes.Role, role.ToString()) };
        if (Request.Headers.ContainsKey(ElevatedHeader))
        {
            claims.Add(new Claim("elevated_until", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString()));
        }

        var identity = new ClaimsIdentity(claims, Scheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
