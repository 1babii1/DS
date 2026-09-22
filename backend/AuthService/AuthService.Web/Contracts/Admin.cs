namespace AuthService.Web.Contracts;

public record AdminAccountSummary(
    Guid Id,
    string Email,
    bool EmailConfirmed,
    bool TwoFactorEnabled,
    IReadOnlyList<string> Roles,
    DateTimeOffset? LockoutEnd,
    DateTime? LastLoginAt,
    string? LastLoginIp);

public record SetRolesRequest(IReadOnlyList<string> Roles);

/// <param name="Until">Null locks indefinitely (DateTimeOffset.MaxValue) - a specific
/// timestamp locks only until then, same shape as Identity's own LockoutEnd.</param>
public record LockAccountRequest(DateTimeOffset? Until);
