using AuthService.Application.Database;
using AuthService.Application.IntegrationEvents;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;

namespace AuthService.Web.Configuration;

/// <summary>
/// Publishes the security-event audit trail (login success/failure, lockout, password
/// change, all-sessions-revoked) to auth.events - shared by AccountController and the
/// matching Razor Pages so both login surfaces produce the same trail, same reasoning as
/// AccountRecoveryService and TwoFactorService. Each method enqueues on the outbox and
/// saves immediately: these calls sit at the end of a request that has no other pending
/// write to piggyback on.
/// </summary>
public class SecurityAuditService(IOutboxWriter outboxWriter, AuthDbContext dbContext, IEmailSender emailSender)
{
    public async Task RecordLoginSucceededAsync(Account user, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.LoginSucceeded,
            user.Id.ToString(),
            new LoginSucceededEvent(user.Id, user.Email!, ipAddress, DateTime.UtcNow));

        // Only once there's a prior IP to compare against - the very first successful
        // login (right after registration) has nothing to be "new" relative to, and
        // notifying then would just tell the user their own first sign-in happened.
        if (ipAddress is not null && user.LastLoginIp is not null && user.LastLoginIp != ipAddress)
        {
            await emailSender.SendNewSignInNotificationAsync(user.Email!, ipAddress, DateTime.UtcNow, cancellationToken);
        }

        user.LastLoginIp = ipAddress ?? user.LastLoginIp;
        user.LastLoginAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordLoginFailedAsync(string email, string reason, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.LoginFailed,
            email,
            new LoginFailedEvent(email, reason, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAccountLockedOutAsync(Account user, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.AccountLockedOut,
            user.Id.ToString(),
            new AccountLockedOutEvent(user.Id, user.Email!, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordPasswordChangedAsync(Account user, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.PasswordChanged,
            user.Id.ToString(),
            new PasswordChangedEvent(user.Id, user.Email!, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAllSessionsRevokedAsync(Account user, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.AllSessionsRevoked,
            user.Id.ToString(),
            new AllSessionsRevokedEvent(user.Id, user.Email!, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAdminRolesChangedAsync(
        Account target, IReadOnlyList<string> roles, Guid performedBy, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.AdminRolesChanged,
            target.Id.ToString(),
            new AdminRolesChangedEvent(target.Id, target.Email!, roles, performedBy, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAdminAccountLockedAsync(
        Account target, DateTimeOffset lockoutEnd, Guid performedBy, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.AdminAccountLocked,
            target.Id.ToString(),
            new AdminAccountLockedEvent(target.Id, target.Email!, lockoutEnd, performedBy, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAdminAccountUnlockedAsync(
        Account target, Guid performedBy, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.AdminAccountUnlocked,
            target.Id.ToString(),
            new AdminAccountUnlockedEvent(target.Id, target.Email!, performedBy, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordEmailChangedAsync(
        Guid accountId, string oldEmail, string newEmail, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.EmailChanged,
            accountId.ToString(),
            new EmailChangedEvent(accountId, oldEmail, newEmail, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordAccountDeletedAsync(Guid accountId, string email, string? ipAddress, CancellationToken cancellationToken)
    {
        outboxWriter.Enqueue(
            AuthEventTypes.AccountDeleted,
            accountId.ToString(),
            new AccountDeletedEvent(accountId, email, ipAddress, DateTime.UtcNow));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
