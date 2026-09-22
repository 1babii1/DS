using AuthService.Domain;
using AuthService.Web.Configuration;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthService.Web.Controllers;

[ApiController]
[Route("auth/external")]
[AllowAnonymous]
public class ExternalLoginController(SignInManager<Account> signInManager, ExternalLoginService externalLogins)
    : ControllerBase
{
    private string? ClientIpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null;

    [HttpGet("google/start")]
    [EnableRateLimiting("auth")]
    public IActionResult StartGoogle([FromQuery] string returnUrl = "/")
    {
        if (!IsAuthorizationReturnUrl(returnUrl))
        {
            return BadRequest();
        }

        var redirectUrl = Url.Action(nameof(GoogleCallback), values: new { returnUrl })!;
        var properties = signInManager.ConfigureExternalAuthenticationProperties(
            GoogleDefaults.AuthenticationScheme, redirectUrl);
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet("google/callback")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> GoogleCallback([FromQuery] string returnUrl = "/")
    {
        if (!IsAuthorizationReturnUrl(returnUrl))
        {
            return BadRequest();
        }

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            return Redirect("/auth/sign-in?error=external_login_failed");
        }

        var (outcome, _) = await externalLogins.CompleteAsync(info, ClientIpAddress, UserAgent, HttpContext.RequestAborted);
        if (outcome == ExternalLoginService.Outcome.TwoFactorRequired)
        {
            return RedirectToPage("/TwoFactor/Login", new { ReturnUrl = returnUrl });
        }

        if (outcome != ExternalLoginService.Outcome.SignedIn)
        {
            return Redirect("/auth/sign-in?error=external_login_failed");
        }

        return LocalRedirect(returnUrl);
    }

    // Same restriction SignIn/TwoFactor Razor Pages already apply to their own ReturnUrl:
    // this login surface exists for exactly one reason - continuing the SPA's OIDC
    // authorization_code flow - so a return url pointing anywhere else is rejected rather
    // than trusted as a generic post-login redirect.
    private bool IsAuthorizationReturnUrl(string returnUrl) => Url.IsLocalUrl(returnUrl)
        && returnUrl.Split('?')[0].Equals("/connect/authorize", StringComparison.Ordinal);
}
