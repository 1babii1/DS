using System.Collections.Immutable;
using System.Security.Claims;
using AuthService.Domain;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Web.Controllers;

public class AuthorizationController(
    UserManager<Account> userManager,
    SignInManager<Account> signInManager,
    IOpenIddictApplicationManager applicationManager,
    IOpenIddictScopeManager scopeManager) : Controller
{
    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded)
        {
            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(
                        Request.HasFormContentType
                            ? Request.Form.ToDictionary(p => p.Key, p => (string?)p.Value.ToString())
                            : Request.Query.ToDictionary(p => p.Key, p => (string?)p.Value.ToString())),
                });
        }

        var user = await userManager.GetUserAsync(result.Principal)
            ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        var application = await applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The application details cannot be found.");

        var identity = new ClaimsIdentity(
            authenticationType: "OpenIddict",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
            .SetClaim(Claims.Email, await userManager.GetEmailAsync(user))
            .SetClaim(Claims.Name, await userManager.GetUserNameAsync(user))
            .SetClaims(Claims.Role, (await userManager.GetRolesAsync(user)).ToImmutableArray());

        identity.SetScopes(request.GetScopes());
        identity.SetResources(await scopeManager.ListResourcesAsync(identity.GetScopes()).ToListAsync());

        foreach (var claim in identity.Claims)
        {
            claim.SetDestinations(GetDestinations(claim, identity));
        }

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

            var user = await userManager.GetUserAsync(result.Principal!);
            if (user is null)
            {
                return Forbid(
                    authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid.",
                    }));
            }

            var identity = new ClaimsIdentity(
                result.Principal!.Claims,
                authenticationType: "OpenIddict",
                nameType: Claims.Name,
                roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, await userManager.GetUserIdAsync(user))
                .SetClaim(Claims.Email, await userManager.GetEmailAsync(user))
                .SetClaim(Claims.Name, await userManager.GetUserNameAsync(user))
                .SetClaims(Claims.Role, (await userManager.GetRolesAsync(user)).ToImmutableArray());

            foreach (var claim in identity.Claims)
            {
                claim.SetDestinations(GetDestinations(claim, identity));
            }

            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            var identity = new ClaimsIdentity(
                authenticationType: "OpenIddict",
                nameType: Claims.Name,
                roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, request.ClientId);
            identity.SetScopes(request.GetScopes());

            foreach (var claim in identity.Claims)
            {
                claim.SetDestinations(GetDestinations(claim, identity));
            }

            return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        throw new NotImplementedException("The specified grant type is not implemented.");
    }

    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    public async Task<IActionResult> Userinfo()
    {
        var user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("The user details cannot be retrieved.");

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = await userManager.GetUserIdAsync(user),
            [Claims.Email] = (await userManager.GetEmailAsync(user))!,
            [Claims.Name] = (await userManager.GetUserNameAsync(user))!,
            [Claims.Role] = await userManager.GetRolesAsync(user),
        };

        return Ok(claims);
    }

    [HttpGet("~/connect/logout")]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();

        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties { RedirectUri = "/" });
    }

    private static IEnumerable<string> GetDestinations(Claim claim, ClaimsIdentity identity)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.Email:
                yield return Destinations.AccessToken;
                if (identity.HasScope(Scopes.Profile) || identity.HasScope(Scopes.Email))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                if (identity.HasScope(Scopes.Roles))
                {
                    yield return Destinations.IdentityToken;
                }

                yield break;

            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
