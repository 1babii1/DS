using AuthService.Domain;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shared;
using Shared.EndpointResults;

namespace AuthService.Web.Controllers;

[ApiController]
[Route("auth/passkeys")]
public class PasskeyController(
    UserManager<Account> userManager,
    SignInManager<Account> signInManager,
    PasskeyService passkeys,
    SecurityAuditService securityAudit,
    AuthSessionService authSessions)
    : ControllerBase
{
    private const string CookieScheme = "Identity.Application";

    private string? ClientIpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null;

    [HttpGet]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    public async Task<IResult> List(CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync();
        return Results.Ok(await passkeys.ListAsync(user.Id, cancellationToken));
    }

    [HttpPost("register/options")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    public async Task<IResult> BeginRegistration(CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync();
        return Results.Ok(await passkeys.BeginRegistrationAsync(user, cancellationToken));
    }

    [HttpPost("register/complete")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> CompleteRegistration(
        [FromBody] CompletePasskeyRegistrationRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync();
        var succeeded = await passkeys.CompleteRegistrationAsync(
            user, request.Token, request.Attestation, request.Label, cancellationToken);
        if (!succeeded)
        {
            return new ErrorResult(Error.Validation(
                "auth.passkey_registration_failed", "That passkey could not be registered", "attestation"));
        }

        return Results.NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    public async Task<IResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var user = await CurrentUserAsync();
        return await passkeys.DeleteAsync(user.Id, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    [HttpPost("login/options")]
    [AllowAnonymous]
    public async Task<IResult> BeginLogin() => Results.Ok(await passkeys.BeginAuthenticationAsync());

    [HttpPost("login/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IResult> CompleteLogin([FromBody] CompletePasskeyLoginRequest request, CancellationToken cancellationToken)
    {
        var user = await passkeys.CompleteAuthenticationAsync(request.Token, request.Assertion, cancellationToken);
        if (user is null)
        {
            return new ErrorResult(Error.Authentication("auth.passkey_login_failed", "Passkey sign-in failed"));
        }

        if (!await userManager.IsEmailConfirmedAsync(user))
        {
            return new ErrorResult(Error.Authentication(
                "auth.email_not_confirmed", "Please confirm your email before signing in"));
        }

        // Not PasswordSignInAsync: there is no password here, and a passkey is already a
        // phishing-resistant strong factor, so this deliberately does not also challenge for
        // TOTP 2FA the way a password login would - re-asking for a second factor after an
        // already-strong factor would just be friction with no security benefit.
        await signInManager.SignInAsync(user, isPersistent: true);
        await securityAudit.RecordLoginSucceededAsync(user, ClientIpAddress, cancellationToken);
        await authSessions.EstablishAsync(user, ClientIpAddress, UserAgent, cancellationToken);

        return Results.NoContent();
    }

    private async Task<Account> CurrentUserAsync() =>
        await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
}
