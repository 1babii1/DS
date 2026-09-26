using System.Security.Claims;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;

namespace AuthService.Web.Configuration;

/// <summary>
/// The account-linking/provisioning decision behind "Sign in with Google" (and any future
/// provider) - kept separate from ExternalLoginController so it can be exercised directly
/// with a hand-built ExternalLoginInfo in tests, without needing a live round trip through
/// Google's own authorization server.
/// </summary>
public class ExternalLoginService(
    UserManager<Account> userManager,
    SignInManager<Account> signInManager,
    SecurityAuditService securityAudit,
    AuthSessionService authSessions)
{
    public enum Outcome
    {
        SignedIn,
        TwoFactorRequired,
        MissingEmail,
        EmailNotVerified,
        AccountCreationFailed,
    }

    public async Task<(Outcome Outcome, Account? User)> CompleteAsync(
        ExternalLoginInfo info, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        // Already linked from a previous sign-in - the common case after the first one.
        // bypassTwoFactor: a successful Google sign-in is itself a strong external factor,
        // the same reasoning PasskeyController.CompleteLogin already applies to passkeys -
        // re-challenging for TOTP on top would be friction with no added security.
        var signInResult = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
        if (signInResult.Succeeded)
        {
            var linkedUser = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey)
                ?? throw new InvalidOperationException("Signed-in external user not found.");
            await securityAudit.RecordLoginSucceededAsync(linkedUser, ipAddress, cancellationToken);
            await authSessions.EstablishAsync(linkedUser, ipAddress, userAgent, cancellationToken);
            return (Outcome.SignedIn, linkedUser);
        }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return (Outcome.MissingEmail, null);
        }

        // Fail closed: linking/provisioning on an email the provider has not verified would let
        // whoever controls that provider account claim any address.
        if (!GoogleClaimMapping.IsEmailVerified(info.Principal))
        {
            await securityAudit.RecordLoginFailedAsync(email, "external_email_not_verified", ipAddress, cancellationToken);
            return (Outcome.EmailNotVerified, null);
        }

        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            // Auto-link on a matching email, no extra confirmation step - but only once BOTH
            // sides have proven ownership. The provider half is checked above. The local half is
            // NOT implied by the row existing: /auth/register creates an account for any address
            // before the owner confirms it, so an attacker can pre-register victim@gmail.com with a
            // password they know. Linking Google to that row would give the real owner an account
            // the attacker can still sign in to (account pre-hijacking, "classic-federated merge").
            // Google's verified email is proof the caller owns the address, so we take the row
            // over: drop whatever password the squatter set, confirm the address, and rotate the
            // security stamp (which also invalidates their outstanding confirmation/reset tokens).
            // An unconfirmed account cannot sign in (RequireConfirmedAccount), so it cannot have
            // sessions, passkeys or 2FA to clean up beyond this.
            if (!existingUser.EmailConfirmed)
            {
                if (await userManager.HasPasswordAsync(existingUser))
                {
                    var removeResult = await userManager.RemovePasswordAsync(existingUser);
                    if (!removeResult.Succeeded)
                    {
                        return (Outcome.AccountCreationFailed, null);
                    }
                }

                existingUser.EmailConfirmed = true;
                existingUser.UpdatedAt = DateTime.UtcNow;
                var confirmResult = await userManager.UpdateAsync(existingUser);
                if (!confirmResult.Succeeded)
                {
                    return (Outcome.AccountCreationFailed, null);
                }

                await userManager.UpdateSecurityStampAsync(existingUser);
            }

            var linkResult = await userManager.AddLoginAsync(
                existingUser, new UserLoginInfo(info.LoginProvider, info.ProviderKey, info.ProviderDisplayName));
            if (!linkResult.Succeeded)
            {
                return (Outcome.AccountCreationFailed, null);
            }

            // The already-linked path above bypasses TOTP on purpose (a Google sign-in is itself a
            // strong external factor). The FIRST link is different: it is decided by an email match
            // alone, so whoever controls that mailbox's Google identity could otherwise walk into an
            // account whose owner enabled 2FA precisely so that mailbox access alone is not enough
            // (a password reset via email still ends at the TOTP prompt). Hand the caller to the
            // normal second-factor challenge instead of signing in.
            if (await userManager.GetTwoFactorEnabledAsync(existingUser))
            {
                var twoFactorResult = await signInManager.ExternalLoginSignInAsync(
                    info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: false);
                return twoFactorResult.RequiresTwoFactor
                    ? (Outcome.TwoFactorRequired, existingUser)
                    : (Outcome.AccountCreationFailed, null);
            }

            await signInManager.SignInAsync(existingUser, isPersistent: true);
            await securityAudit.RecordLoginSucceededAsync(existingUser, ipAddress, cancellationToken);
            await authSessions.EstablishAsync(existingUser, ipAddress, userAgent, cancellationToken);
            return (Outcome.SignedIn, existingUser);
        }

        // Brand new account, no password - Google already verified this email, the same
        // trust level EmployeeEventsConsumer relies on for HR-provisioned accounts and
        // OpenIddictSeeder relies on for the seed admin.
        var user = new Account
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            return (Outcome.AccountCreationFailed, null);
        }

        await userManager.AddToRoleAsync(user, RoleNames.Viewer);
        await userManager.AddLoginAsync(user, new UserLoginInfo(info.LoginProvider, info.ProviderKey, info.ProviderDisplayName));
        await signInManager.SignInAsync(user, isPersistent: true);
        await securityAudit.RecordLoginSucceededAsync(user, ipAddress, cancellationToken);
        await authSessions.EstablishAsync(user, ipAddress, userAgent, cancellationToken);
        return (Outcome.SignedIn, user);
    }
}
