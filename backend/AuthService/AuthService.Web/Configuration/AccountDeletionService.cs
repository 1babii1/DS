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
    IOpenIddictTokenManager tokenManager,
    SignInManager<Account> signInManager,
    SecurityAuditService securityAudit)
{
    public enum Outcome
    {
        Success,
        WrongPassword,
        LastAdmin,
        DeletionFailed,
        NotFound,
        CannotEraseSelf,
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

        var outcome = await RemoveAsync(user, ipAddress, cancellationToken);
        if (outcome != Outcome.Success)
        {
            return outcome;
        }

        await signInManager.SignOutAsync();

        return Outcome.Success;
    }

    /// <summary>
    /// Erasure on an operator's say-so (ADR 0049): the same removal, without the password, because the person may no longer be
    /// reachable. Not the operator's own account (that is what self-service is for), and never the last admin.
    /// </summary>
    public async Task<Outcome> EraseAsync(Guid targetId, Guid performedBy, string? ipAddress, CancellationToken cancellationToken)
    {
        if (targetId == performedBy)
        {
            return Outcome.CannotEraseSelf;
        }

        var user = await userManager.FindByIdAsync(targetId.ToString());
        if (user is null)
        {
            return Outcome.NotFound;
        }

        if (await userManager.IsInRoleAsync(user, RoleNames.Admin) && (await userManager.GetUsersInRoleAsync(RoleNames.Admin)).Count <= 1)
        {
            return Outcome.LastAdmin;
        }

        return await RemoveAsync(user, ipAddress, cancellationToken);
    }

    // What both paths remove. The tokens are revoked as well as the authorizations: a refresh token must stop working the moment the
    // account is gone, not when its authorization is next looked at. The event that records the deletion names the account by id and
    // carries neither the address nor the IP (ADR 0049): it is published to a bus that keeps it, and the person asked not to be kept.
    private async Task<Outcome> RemoveAsync(Account user, string? ipAddress, CancellationToken cancellationToken)
    {
        var accountId = user.Id;

        await tokenManager.RevokeBySubjectAsync(accountId.ToString());
        await authorizationManager.RevokeBySubjectAsync(accountId.ToString());
        await dbContext.PasskeyCredentials.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AuthSessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);

        var deleteResult = await userManager.DeleteAsync(user);
        if (!deleteResult.Succeeded)
        {
            return Outcome.DeletionFailed;
        }

        await securityAudit.RecordAccountDeletedAsync(accountId, cancellationToken);
        return Outcome.Success;
    }
}
