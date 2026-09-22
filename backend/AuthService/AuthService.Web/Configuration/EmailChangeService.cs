using AuthService.Application;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AuthService.Web.Configuration;

/// <summary>
/// Changing the email on an account is a step toward taking it over (it becomes the
/// destination for the next password reset), so this requires the current password up
/// front and never changes anything until the *new* address confirms it can receive mail
/// for it - the same "prove it before it takes effect" shape as ResetPasswordAsync,
/// applied to the address itself rather than the credential.
/// </summary>
public class EmailChangeService(
    UserManager<Account> userManager,
    IEmailSender emailSender,
    AccountRecoveryService recovery,
    IOptions<AuthOptions> authOptions,
    SecurityAuditService securityAudit)
{
    public enum RequestOutcome
    {
        Success,
        WrongPassword,
        EmailInUse,
    }

    public async Task<RequestOutcome> RequestChangeAsync(
        Account user, string newEmail, string password, CancellationToken cancellationToken)
    {
        if (!await userManager.CheckPasswordAsync(user, password))
        {
            return RequestOutcome.WrongPassword;
        }

        if (await userManager.FindByEmailAsync(newEmail) is not null)
        {
            return RequestOutcome.EmailInUse;
        }

        var token = await userManager.GenerateChangeEmailTokenAsync(user, newEmail);
        var link = $"{authOptions.Value.Issuer.TrimEnd('/')}/auth/email/confirm-change" +
            $"?userId={user.Id}&newEmail={Uri.EscapeDataString(newEmail)}&token={Uri.EscapeDataString(token)}";
        await emailSender.SendEmailChangeConfirmationAsync(newEmail, link, cancellationToken);
        return RequestOutcome.Success;
    }

    /// <returns>False on any failure (unknown account, invalid/expired/reused token, or the
    /// new email having been claimed by someone else in the meantime) - deliberately one
    /// outcome shape, matching ResetPasswordAsync's own reasoning for not distinguishing
    /// "no such account" from "bad token" to the caller.</returns>
    public async Task<bool> CompleteChangeAsync(
        Guid userId, string newEmail, string token, string? ipAddress, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return false;
        }

        var oldEmail = user.Email!;
        var result = await userManager.ChangeEmailAsync(user, newEmail, token);
        if (!result.Succeeded)
        {
            return false;
        }

        // ChangeEmailAsync only ever touches Email/EmailConfirmed - UserName stays whatever
        // it was before, which would silently drift from Email (this project's own invariant
        // since registration always sets UserName = Email) unless kept in sync here.
        await userManager.SetUserNameAsync(user, newEmail);

        // Same reasoning as ResetPasswordAsync: the email that receives the next password
        // reset just changed, which is exactly the kind of event that should not leave any
        // pre-existing session or refresh token still valid.
        await recovery.RevokeAllSessionsAsync(user, ipAddress, cancellationToken);
        await securityAudit.RecordEmailChangedAsync(user.Id, oldEmail, newEmail, ipAddress, cancellationToken);

        return true;
    }
}
