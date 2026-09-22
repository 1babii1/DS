using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace AuthService.Web.Configuration;

/// <summary>
/// Self-service account deletion. Password-gated like TwoFactorService.DisableAsync and
/// RegenerateRecoveryCodesAsync - the same "prove it's really you" bar as every other
/// destructive account action on the cookie scheme, where StepUp's claim (bearer-only)
/// cannot apply. PasskeyCredentials and AuthSessions have no FK relationship to Account
/// (neither is Identity's own table), so deleting the account would silently orphan them
/// unless cleaned up explicitly here first.
/// </summary>
public class AccountDeletionService(
    UserManager<Account> userManager,
    AuthDbContext dbContext,
    IOpenIddictAuthorizationManager authorizationManager,
    SignInManager<Account> signInManager,
    SecurityAuditService securityAudit)
{
    public enum Outcome
    {
        Success,
        WrongPassword,
        LastAdmin,
        DeletionFailed,
    }

    public async Task<Outcome> DeleteAsync(Account user, string password, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!await userManager.CheckPasswordAsync(user, password))
        {
            return Outcome.WrongPassword;
        }

        // The one footgun worth blocking outright: deleting the sole admin would leave the
        // admin panel with nobody able to sign in to it - the same reasoning
        // AdminAccountService.SetRolesAsync already applies to an admin removing their own
        // role, just reached through self-deletion instead.
        if (await userManager.IsInRoleAsync(user, RoleNames.Admin))
        {
            var admins = await userManager.GetUsersInRoleAsync(RoleNames.Admin);
            if (admins.Count <= 1)
            {
                return Outcome.LastAdmin;
            }
        }

        var accountId = user.Id;
        var email = user.Email!;

        await authorizationManager.RevokeBySubjectAsync(accountId.ToString());
        await dbContext.PasskeyCredentials.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AuthSessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);

        var deleteResult = await userManager.DeleteAsync(user);
        if (!deleteResult.Succeeded)
        {
            return Outcome.DeletionFailed;
        }

        await securityAudit.RecordAccountDeletedAsync(accountId, email, ipAddress, cancellationToken);
        await signInManager.SignOutAsync();

        return Outcome.Success;
    }
}
