namespace AuthService.Application.IntegrationEvents;

public static class AuthEventTypes
{
    public const string AccountProvisioned = "AccountProvisioned";
    public const string AccountProvisioningFailed = "AccountProvisioningFailed";
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string AccountLockedOut = "AccountLockedOut";
    public const string PasswordChanged = "PasswordChanged";
    public const string AllSessionsRevoked = "AllSessionsRevoked";
    public const string AdminRolesChanged = "AdminRolesChanged";
    public const string AdminAccountLocked = "AdminAccountLocked";
    public const string AdminAccountUnlocked = "AdminAccountUnlocked";
    public const string EmailChanged = "EmailChanged";
    public const string AccountDeleted = "AccountDeleted";
}

public record AccountProvisionedEvent(Guid EmployeeId, Guid AccountId);

public record AccountProvisioningFailedEvent(Guid EmployeeId, string Reason, Guid? HiredByAccountId = null);

public record LoginSucceededEvent(Guid AccountId, string Email, string? IpAddress, DateTime OccurredAt);

/// <param name="Reason">"invalid_credentials", "email_not_confirmed", "two_factor_invalid", or
/// "recovery_code_invalid" - the same internal classification the caller already branched on,
/// kept distinct from the generic client-facing error message for defenders reading the trail.</param>
public record LoginFailedEvent(string Email, string Reason, string? IpAddress, DateTime OccurredAt);

public record AccountLockedOutEvent(Guid AccountId, string Email, string? IpAddress, DateTime OccurredAt);

public record PasswordChangedEvent(Guid AccountId, string Email, string? IpAddress, DateTime OccurredAt);

public record AllSessionsRevokedEvent(Guid AccountId, string Email, string? IpAddress, DateTime OccurredAt);

public record AdminRolesChangedEvent(
    Guid TargetAccountId, string TargetEmail, IReadOnlyList<string> Roles, Guid PerformedByAccountId, string? IpAddress, DateTime OccurredAt);

public record AdminAccountLockedEvent(
    Guid TargetAccountId, string TargetEmail, DateTimeOffset LockoutEnd, Guid PerformedByAccountId, string? IpAddress, DateTime OccurredAt);

public record AdminAccountUnlockedEvent(
    Guid TargetAccountId, string TargetEmail, Guid PerformedByAccountId, string? IpAddress, DateTime OccurredAt);

public record EmailChangedEvent(Guid AccountId, string OldEmail, string NewEmail, string? IpAddress, DateTime OccurredAt);

public record AccountDeletedEvent(Guid AccountId, string Email, string? IpAddress, DateTime OccurredAt);
