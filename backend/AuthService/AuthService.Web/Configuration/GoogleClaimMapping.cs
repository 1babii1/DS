using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace AuthService.Web.Configuration;

/// <summary>
/// ASP.NET Core's Google handler maps the userinfo email but NOT whether Google has verified it.
/// Linking or provisioning an account on an unverified provider email is the "non-verifying IdP"
/// pre-hijacking variant, so the flag is mapped here and ExternalLoginService fails closed on it.
/// </summary>
public static class GoogleClaimMapping
{
    public const string EmailVerifiedClaim = "email_verified";

    public static void Apply(OAuthOptions options) =>
        options.ClaimActions.MapJsonKey(EmailVerifiedClaim, "email_verified");

    /// <summary>Fails closed: a missing claim is "not verified". The JSON boolean arrives as
    /// "True"/"False", hence the case-insensitive comparison.</summary>
    public static bool IsEmailVerified(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);
}
