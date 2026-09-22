using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace AuthService.Web.Configuration;

/// <summary>
/// AuthService's account-management surface for admins: search/inspect accounts, change
/// roles, lock/unlock. Every mutation goes through here (never the controller directly) so
/// the self-targeting guards and the audit trail can't be bypassed by a future second
/// caller, the same reasoning behind every other *Service in this folder.
/// </summary>
public class AdminAccountService(
    UserManager<Account> userManager,
    AuthDbContext dbContext,
    AccountRecoveryService recovery,
    SecurityAuditService securityAudit)
{
    public enum Outcome
    {
        Success,
        NotFound,
        InvalidRoles,
        CannotTargetSelf,
    }

    public async Task<PagedResponse<AdminAccountSummary>> SearchAsync(
        string? search, int? page, int? size, CancellationToken cancellationToken)
    {
        var (normalizedPage, normalizedSize) = PagedResponse<AdminAccountSummary>.Normalize(page, size);

        IQueryable<Account> query = dbContext.Users.AsNoTracking().OrderBy(a => a.Email);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(a => EF.Functions.ILike(a.Email!, pattern));
        }

        var total = await query.CountAsync(cancellationToken);
        var accounts = await query
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .ToListAsync(cancellationToken);

        var items = new List<AdminAccountSummary>(accounts.Count);
        foreach (var account in accounts)
        {
            items.Add(await ToSummaryAsync(account));
        }

        return new PagedResponse<AdminAccountSummary>(items, normalizedPage, normalizedSize, total);
    }

    public async Task<AdminAccountSummary?> GetByIdAsync(Guid accountId)
    {
        var account = await userManager.FindByIdAsync(accountId.ToString());
        return account is null ? null : await ToSummaryAsync(account);
    }

    /// <summary>Replaces the account's role set. Rejects removing the caller's own admin
    /// role - the classic footgun of an admin locking themselves out of the admin panel
    /// with nobody left able to undo it.</summary>
    public async Task<Outcome> SetRolesAsync(
        Guid targetId, Guid performedById, IReadOnlyList<string> roles, string? ipAddress, CancellationToken cancellationToken)
    {
        if (roles.Any(r => !RoleNames.All.Contains(r)))
        {
            return Outcome.InvalidRoles;
        }

        if (targetId == performedById && !roles.Contains(RoleNames.Admin))
        {
            return Outcome.CannotTargetSelf;
        }

        var account = await userManager.FindByIdAsync(targetId.ToString());
        if (account is null)
        {
            return Outcome.NotFound;
        }

        var currentRoles = await userManager.GetRolesAsync(account);
        var toRemove = currentRoles.Except(roles).ToList();
        var toAdd = roles.Except(currentRoles).ToList();
        if (toRemove.Count > 0)
        {
            await userManager.RemoveFromRolesAsync(account, toRemove);
        }

        if (toAdd.Count > 0)
        {
            await userManager.AddToRolesAsync(account, toAdd);
        }

        await securityAudit.RecordAdminRolesChangedAsync(account, roles, performedById, ipAddress, cancellationToken);
        return Outcome.Success;
    }

    /// <summary>Locks the account and immediately revokes its existing sessions/tokens -
    /// a lock that only blocked future logins would leave an already-issued refresh token
    /// or cookie working for the rest of its lifetime, defeating the point of locking a
    /// compromised or terminated account right now.</summary>
    public async Task<Outcome> LockAsync(
        Guid targetId, Guid performedById, DateTimeOffset until, string? ipAddress, CancellationToken cancellationToken)
    {
        if (targetId == performedById)
        {
            return Outcome.CannotTargetSelf;
        }

        var account = await userManager.FindByIdAsync(targetId.ToString());
        if (account is null)
        {
            return Outcome.NotFound;
        }

        await userManager.SetLockoutEndDateAsync(account, until);
        await recovery.RevokeAllSessionsAsync(account, ipAddress, cancellationToken);
        await securityAudit.RecordAdminAccountLockedAsync(account, until, performedById, ipAddress, cancellationToken);
        return Outcome.Success;
    }

    public async Task<Outcome> UnlockAsync(
        Guid targetId, Guid performedById, string? ipAddress, CancellationToken cancellationToken)
    {
        var account = await userManager.FindByIdAsync(targetId.ToString());
        if (account is null)
        {
            return Outcome.NotFound;
        }

        await userManager.SetLockoutEndDateAsync(account, null);
        await userManager.ResetAccessFailedCountAsync(account);
        await securityAudit.RecordAdminAccountUnlockedAsync(account, performedById, ipAddress, cancellationToken);
        return Outcome.Success;
    }

    /// <summary>Force-revoke without locking - "make them sign in again" as a lighter
    /// action than a lock, e.g. after a suspected but unconfirmed compromise.</summary>
    public async Task<Outcome> RevokeSessionsAsync(
        Guid targetId, Guid performedById, string? ipAddress, CancellationToken cancellationToken)
    {
        var account = await userManager.FindByIdAsync(targetId.ToString());
        if (account is null)
        {
            return Outcome.NotFound;
        }

        await recovery.RevokeAllSessionsAsync(account, ipAddress, cancellationToken);
        return Outcome.Success;
    }

    private async Task<AdminAccountSummary> ToSummaryAsync(Account account) =>
        new(
            account.Id,
            account.Email!,
            account.EmailConfirmed,
            await userManager.GetTwoFactorEnabledAsync(account),
            (await userManager.GetRolesAsync(account)).ToList(),
            await userManager.GetLockoutEndDateAsync(account),
            account.LastLoginAt,
            account.LastLoginIp);
}
