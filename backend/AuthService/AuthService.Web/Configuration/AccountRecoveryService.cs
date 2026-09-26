using AuthService.Application;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shared.Database;

namespace AuthService.Web.Configuration;

/// <summary>
/// Owns the enumeration-safe shape of "send a confirmation or reset link", the
/// reset-then-revoke sequence a redeemed reset token triggers, and the standalone
/// "sign out everywhere" action that revokes the same way without a password change.
/// AccountController (the JSON API) and the Password/Email Razor Pages both need the
/// exact same decisions here, and having them in two places would risk the two drifting
/// apart on exactly the security properties that matter.
/// </summary>
public class AccountRecoveryService(
    UserManager<Account> userManager,
    IEmailSender emailSender,
    IOpenIddictAuthorizationManager authorizationManager,
    IOptions<AuthOptions> authOptions,
    SecurityAuditService securityAudit,
    AuthSessionService authSessions)
{
    public async Task SendConfirmationEmailAsync(Account user, CancellationToken cancellationToken)
    {
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = BuildLink("/auth/email/confirm", user.Email!, token);

        await emailSender.SendEmailConfirmationAsync(user.Email!, link, cancellationToken);
    }

    /// <returns>
    /// Null on success OR on a duplicate address - both cases end in 204 with nothing in the
    /// response distinguishing them, which is the entire point (see below). A non-null, failed
    /// IdentityResult is a genuine validation problem (weak password, etc.); the caller maps that
    /// to its usual 400 body exactly as it did with CreateAsync's own result before this method
    /// existed - a duplicate address never reaches that path, so it never carries
    /// DuplicateUserName/DuplicateEmail in a body a caller can read.
    ///
    /// Without this, /auth/register is a stock enumeration oracle: Identity's own
    /// RequireUniqueEmail check makes a second registration to the same address return both a
    /// different status code AND a body that says "is already taken" (confirmed empirically
    /// against this project's own test before this method existed) - exactly what the
    /// neighbouring ForgotPassword/ResetPassword flows above already take care not to do. The
    /// email address itself still learns about the attempt either way (a real confirmation link
    /// for a genuinely new address, or a notice for one that already has an account), so only
    /// whoever controls that mailbox, never the caller, can tell the two cases apart.
    /// </returns>
    public async Task<IdentityResult?> CompleteRegistrationAsync(
        string email, string password, CancellationToken cancellationToken)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            await emailSender.SendDuplicateRegistrationNoticeAsync(email, cancellationToken);
            return null;
        }

        var user = new Account
        {
            UserName = email,
            Email = email,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        IdentityResult result;
        try
        {
            result = await userManager.CreateAsync(user, password);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // A second registration racing the first between the check above and this call: two
            // concurrent CreateAsync calls can both pass Identity's own pre-insert uniqueness
            // check (itself a check-then-act) before either commits, so what actually stops the
            // second insert is the database's unique index - which Identity does not translate
            // into a graceful DuplicateUserName/DuplicateEmail IdentityResult, it just lets the
            // constraint violation surface as an unhandled exception. Confirmed empirically (this
            // 500 was reproduced via Concurrent_registrations_to_the_same_email_create_exactly_
            // one_account before this catch existed) rather than assumed from how the
            // non-concurrent path behaves.
            await emailSender.SendDuplicateRegistrationNoticeAsync(email, cancellationToken);
            return null;
        }

        if (!result.Succeeded)
        {
            // The non-racing duplicate path: Identity's own pre-insert check caught it before any
            // database write was attempted, so it returns gracefully instead of throwing.
            if (result.Errors.Any(error => error.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                await emailSender.SendDuplicateRegistrationNoticeAsync(email, cancellationToken);
                return null;
            }

            return result;
        }

        await userManager.AddToRoleAsync(user, RoleNames.Viewer);

        // Deliberately no SignInAsync here: that call does not consult RequireConfirmedAccount
        // the way PasswordSignInAsync does, so calling it would silently hand out an
        // authenticated session to an account nobody has verified the email address for yet.
        await SendConfirmationEmailAsync(user, cancellationToken);
        return IdentityResult.Success;
    }

    /// <summary>
    /// Only sends when the account exists and is not already confirmed; the caller
    /// always reports success regardless, so a prober cannot tell "sent" apart from
    /// "nothing to send" from the response alone.
    /// </summary>
    public async Task ResendConfirmationIfEligibleAsync(string email, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is not null && !await userManager.IsEmailConfirmedAsync(user))
        {
            await SendConfirmationEmailAsync(user, cancellationToken);
        }
    }

    /// <summary>
    /// Only sends when the account exists; the caller always reports success regardless,
    /// same enumeration-safety shape as <see cref="ResendConfirmationIfEligibleAsync"/>.
    /// </summary>
    public async Task SendPasswordResetIfEligibleAsync(string email, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            return;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var link = BuildLink("/auth/password/reset", email, token);

        await emailSender.SendPasswordResetAsync(email, link, cancellationToken);
    }

    /// <returns>
    /// The IdentityResult from ResetPasswordAsync - the caller maps a failure to a generic
    /// "invalid or expired" response rather than surfacing IdentityError details, since
    /// those would otherwise distinguish "no such account" from "bad token" here.
    /// </returns>
    public async Task<IdentityResult> ResetPasswordAsync(
        string email, string token, string newPassword, string? ipAddress, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            return IdentityResult.Failed(new IdentityError { Description = "Invalid token." });
        }

        var result = await userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded)
        {
            return result;
        }

        // ResetPasswordAsync already rotates the Identity security stamp, which signs the
        // browser cookie session out on its next validation - but OpenIddict validates its
        // own issued tokens independently of that stamp, so a refresh token obtained before
        // this reset would otherwise keep working for up to its own 30-day lifetime even
        // after the password that granted it no longer does.
        await authorizationManager.RevokeBySubjectAsync(user.Id.ToString());
        await securityAudit.RecordPasswordChangedAsync(user, ipAddress, cancellationToken);

        return result;
    }

    /// <summary>
    /// Revokes every OpenIddict authorization (all clients, all refresh tokens) for the
    /// account and rotates its Identity security stamp, which signs the Identity.Application
    /// cookie out everywhere on its next validation - the same two effects ResetPasswordAsync
    /// gets from Identity's own internal stamp rotation plus its explicit revoke call, done
    /// here directly since there is no password change to piggyback on.
    /// </summary>
    public async Task RevokeAllSessionsAsync(Account user, string? ipAddress, CancellationToken cancellationToken)
    {
        await authorizationManager.RevokeBySubjectAsync(user.Id.ToString());
        await userManager.UpdateSecurityStampAsync(user);
        await authSessions.RevokeAllAsync(user.Id, cancellationToken);
        await securityAudit.RecordAllSessionsRevokedAsync(user, ipAddress, cancellationToken);
    }

    private string BuildLink(string path, string email, string token) =>
        $"{authOptions.Value.Issuer.TrimEnd('/')}{path}" +
        $"?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}
