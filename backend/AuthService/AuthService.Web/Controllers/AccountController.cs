using System.Security.Claims;
using AuthService.Domain;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shared;
using Shared.EndpointResults;

namespace AuthService.Web.Controllers;

[ApiController]
[Route("auth")]
public class AccountController(
    UserManager<Account> userManager,
    SignInManager<Account> signInManager,
    AccountRecoveryService recovery,
    TwoFactorService twoFactor,
    SecurityAuditService securityAudit,
    StepUpService stepUp,
    AuthSessionService authSessions,
    EmailChangeService emailChange,
    AccountDeletionService accountDeletion)
    : ControllerBase
{
    private const string CookieScheme = "Identity.Application";

    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null;

    private string? ClientIpAddress => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        // AccountRecoveryService.CompleteRegistrationAsync owns the enumeration-safe shape of
        // this (an existing address gets the same 204 a fresh registration does, never Identity's
        // own DuplicateUserName/DuplicateEmail body) - see its own doc comment for why.
        var result = await recovery.CompleteRegistrationAsync(request.Email, request.Password, cancellationToken);
        if (result is not null && !result.Succeeded)
        {
            return new ErrorResult(ToValidationError(result));
        }

        return Results.NoContent();
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await signInManager.PasswordSignInAsync(
            request.Email,
            request.Password,
            isPersistent: true,
            lockoutOnFailure: true);

        if (result.RequiresTwoFactor)
        {
            // Not signed in yet: PasswordSignInAsync already set the short-lived
            // Identity.TwoFactorUserId cookie identifying who is mid-login, without
            // granting the full Identity.Application session. The caller submits the
            // code (or a recovery code) to /auth/login/2fa[-recovery] next, where the
            // completed attempt is what gets audited.
            return Results.Ok(new { requiresTwoFactor = true });
        }

        if (result.IsLockedOut)
        {
            // The account exists (PasswordSignInAsync only reaches IsLockedOut after
            // finding one), so this is the one non-2FA branch that can resolve a user
            // for the audit trail without a second lookup risking an enumeration signal
            // in the response - the response itself stays generic below.
            var lockedOutUser = await userManager.FindByEmailAsync(request.Email);
            if (lockedOutUser is not null)
            {
                await securityAudit.RecordAccountLockedOutAsync(lockedOutUser, ClientIpAddress, cancellationToken);
            }

            return new ErrorResult(Error.Authentication("auth.invalid_credentials", "Invalid email or password"));
        }

        if (result.IsNotAllowed)
        {
            // Distinct from invalid_credentials: the caller supplied a correct password,
            // so this reveals nothing an attacker could not already tell from a successful
            // credential guess. Not accepting the login is what RequireConfirmedAccount is
            // for - only unconfirmed reaches this branch (lockout has its own message).
            await securityAudit.RecordLoginFailedAsync(
                request.Email, "email_not_confirmed", ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Authentication(
                "auth.email_not_confirmed", "Please confirm your email before signing in"));
        }

        if (!result.Succeeded)
        {
            await securityAudit.RecordLoginFailedAsync(
                request.Email, "invalid_credentials", ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Authentication("auth.invalid_credentials", "Invalid email or password"));
        }

        var user = await userManager.FindByEmailAsync(request.Email)
            ?? throw new InvalidOperationException("Signed-in user not found.");
        await securityAudit.RecordLoginSucceededAsync(user, ClientIpAddress, cancellationToken);
        await authSessions.EstablishAsync(user, ClientIpAddress, UserAgent, cancellationToken);

        return Results.NoContent();
    }

    [HttpPost("login/2fa")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> LoginTwoFactor([FromBody] TwoFactorLoginRequest request, CancellationToken cancellationToken)
    {
        var pendingUser = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (pendingUser is null)
        {
            // No pending Identity.TwoFactorUserId cookie - either it expired, was never
            // set (this endpoint was called without a prior Login that required 2FA), or
            // was already consumed. Same error either way: nothing here to distinguish.
            return new ErrorResult(Error.Authentication(
                "auth.two_factor_session_expired", "Your sign-in session expired - start again"));
        }

        var result = await twoFactor.CompleteWithAuthenticatorCodeAsync(request.Code, request.RememberClient);
        if (result.IsLockedOut)
        {
            await securityAudit.RecordAccountLockedOutAsync(pendingUser, ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Authentication("auth.two_factor_code_invalid", "Invalid authenticator code"));
        }

        if (!result.Succeeded)
        {
            await securityAudit.RecordLoginFailedAsync(
                pendingUser.Email!, "two_factor_invalid", ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Authentication("auth.two_factor_code_invalid", "Invalid authenticator code"));
        }

        await securityAudit.RecordLoginSucceededAsync(pendingUser, ClientIpAddress, cancellationToken);
        await authSessions.EstablishAsync(pendingUser, ClientIpAddress, UserAgent, cancellationToken);
        return Results.NoContent();
    }

    [HttpPost("login/2fa-recovery")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> LoginTwoFactorRecovery(
        [FromBody] TwoFactorRecoveryLoginRequest request, CancellationToken cancellationToken)
    {
        var pendingUser = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (pendingUser is null)
        {
            return new ErrorResult(Error.Authentication(
                "auth.two_factor_session_expired", "Your sign-in session expired - start again"));
        }

        var result = await twoFactor.CompleteWithRecoveryCodeAsync(request.RecoveryCode);
        if (result.IsLockedOut)
        {
            await securityAudit.RecordAccountLockedOutAsync(pendingUser, ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Authentication("auth.recovery_code_invalid", "Invalid recovery code"));
        }

        if (!result.Succeeded)
        {
            await securityAudit.RecordLoginFailedAsync(
                pendingUser.Email!, "recovery_code_invalid", ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Authentication("auth.recovery_code_invalid", "Invalid recovery code"));
        }

        await securityAudit.RecordLoginSucceededAsync(pendingUser, ClientIpAddress, cancellationToken);
        await authSessions.EstablishAsync(pendingUser, ClientIpAddress, UserAgent, cancellationToken);
        return Results.NoContent();
    }

    [HttpGet("2fa/setup")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    public async Task<IResult> TwoFactorSetup()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        return Results.Ok(await twoFactor.GetOrCreateSetupAsync(user));
    }

    [HttpPost("2fa/enable")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> EnableTwoFactor([FromBody] Enable2faRequest request)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        var recoveryCodes = await twoFactor.EnableAsync(user, request.Code);
        if (recoveryCodes is null)
        {
            return new ErrorResult(Error.Validation("auth.two_factor_code_invalid", "Invalid authenticator code", "code"));
        }

        return Results.Ok(new Enable2faResponse(recoveryCodes));
    }

    [HttpPost("2fa/disable")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> DisableTwoFactor([FromBody] Disable2faRequest request)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        if (!await twoFactor.DisableAsync(user, request.Password))
        {
            return new ErrorResult(Error.Validation("auth.password_invalid", "Incorrect password", "password"));
        }

        return Results.NoContent();
    }

    [HttpPost("2fa/recovery-codes")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> RegenerateRecoveryCodes([FromBody] RegenerateRecoveryCodesRequest request)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        var codes = await twoFactor.RegenerateRecoveryCodesAsync(user, request.Password);
        if (codes is null)
        {
            return new ErrorResult(Error.Validation("auth.password_invalid", "Incorrect password", "password"));
        }

        return Results.Ok(new RegenerateRecoveryCodesResponse(codes));
    }

    [HttpGet("step-up/status")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    public async Task<IResult> StepUpStatus()
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        var elevatedUntil = user.ElevatedUntil is { } until && until > DateTime.UtcNow ? until : (DateTime?)null;
        return Results.Ok(new StepUpStatusResponse(await userManager.GetTwoFactorEnabledAsync(user), elevatedUntil));
    }

    [HttpPost("step-up/request-email-code")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> RequestStepUpEmailCode(CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        if (!await stepUp.RequestEmailCodeAsync(user, cancellationToken))
        {
            // 2FA-enabled accounts confirm with their authenticator code directly - there is
            // no email fallback to request.
            return new ErrorResult(Error.Validation(
                "auth.step_up_email_not_applicable", "This account uses an authenticator app instead", "code"));
        }

        return Results.NoContent();
    }

    [HttpPost("step-up/verify")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> VerifyStepUp([FromBody] StepUpVerifyRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        if (!await stepUp.VerifyAsync(user, request.Code))
        {
            await securityAudit.RecordLoginFailedAsync(user.Email!, "step_up_invalid", ClientIpAddress, cancellationToken);
            return new ErrorResult(Error.Validation("auth.step_up_code_invalid", "Invalid confirmation code", "code"));
        }

        // Takes effect the next time the client exchanges its refresh token - the access
        // token already in hand was minted before this call and cannot retroactively carry
        // the new elevated_until claim, so a sensitive request made with it still gets
        // rejected by the StepUp policy until the client refreshes.
        return Results.NoContent();
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = "Identity.Application")]
    public async Task<IResult> Logout()
    {
        await signInManager.SignOutAsync();
        return Results.NoContent();
    }

    [HttpPost("sessions/revoke-all")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> RevokeAllSessions(CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        await recovery.RevokeAllSessionsAsync(user, ClientIpAddress, cancellationToken);

        // The revoke above already invalidates the current cookie on its next validation
        // (security stamp rotation), but signing out explicitly here ends it immediately
        // instead of leaving this same request's caller technically still logged in until
        // that next check.
        await signInManager.SignOutAsync();
        return Results.NoContent();
    }

    [HttpGet("sessions")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    public async Task<IResult> ListSessions(CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        var currentSessionId = Guid.TryParse(User.FindFirstValue(AuthSessionService.SessionIdClaimType), out var id)
            ? id
            : (Guid?)null;
        return Results.Ok(await authSessions.ListAsync(user.Id, currentSessionId, cancellationToken));
    }

    [HttpDelete("sessions/{id:guid}")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> RevokeSession(Guid id, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        return await authSessions.RevokeAsync(user.Id, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    [HttpPost("email/request-change")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> RequestEmailChange([FromBody] RequestEmailChangeRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        var outcome = await emailChange.RequestChangeAsync(user, request.NewEmail, request.Password, cancellationToken);
        return outcome switch
        {
            EmailChangeService.RequestOutcome.Success => Results.NoContent(),
            EmailChangeService.RequestOutcome.WrongPassword => new ErrorResult(
                Error.Validation("auth.password_invalid", "Incorrect password", "password")),
            EmailChangeService.RequestOutcome.EmailInUse => new ErrorResult(
                Error.Validation("auth.email_in_use", "That email is already in use", "newEmail")),
            _ => throw new InvalidOperationException($"Unhandled outcome: {outcome}"),
        };
    }

    [HttpDelete("account")]
    [Authorize(AuthenticationSchemes = CookieScheme)]
    [EnableRateLimiting("auth")]
    public async Task<IResult> DeleteAccount([FromBody] DeleteAccountRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User) ?? throw new InvalidOperationException("Signed-in user not found.");
        var outcome = await accountDeletion.DeleteAsync(user, request.Password, ClientIpAddress, cancellationToken);
        return outcome switch
        {
            AccountDeletionService.Outcome.Success => Results.NoContent(),
            AccountDeletionService.Outcome.WrongPassword => new ErrorResult(
                Error.Validation("auth.password_invalid", "Incorrect password", "password")),
            AccountDeletionService.Outcome.LastAdmin => new ErrorResult(Error.Validation(
                "auth.cannot_delete_last_admin", "You are the only admin - promote another account first", "password")),
            AccountDeletionService.Outcome.DeletionFailed => new ErrorResult(
                Error.Validation("auth.account_deletion_failed", "Your account could not be deleted", "password")),
            _ => throw new InvalidOperationException($"Unhandled outcome: {outcome}"),
        };
    }

    [HttpPost("resend-confirmation")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> ResendConfirmation(
        [FromBody] ResendConfirmationRequest request, CancellationToken cancellationToken)
    {
        await recovery.ResendConfirmationIfEligibleAsync(request.Email, cancellationToken);
        return Results.NoContent();
    }

    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await recovery.SendPasswordResetIfEligibleAsync(request.Email, cancellationToken);
        return Results.NoContent();
    }

    [HttpPost("reset-password")]
    [EnableRateLimiting("auth")]
    public async Task<IResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await recovery.ResetPasswordAsync(
            request.Email, request.Token, request.NewPassword, ClientIpAddress, cancellationToken);
        if (!result.Succeeded)
        {
            // Generic on purpose: a missing account and an invalid/expired token must not
            // be distinguishable from the response.
            return new ErrorResult(Error.Validation(
                "auth.reset_token_invalid", "This password reset link is invalid or has expired", "token"));
        }

        return Results.NoContent();
    }

    private static Error ToValidationError(IdentityResult result)
    {
        var messages = result.Errors.Select(error => new ErrorMessage(error.Code, error.Description));
        return Error.Validation(messages);
    }
}
