namespace AuthService.Web.Contracts;

public record AuthSessionSummary(Guid Id, string? IpAddress, string? UserAgent, DateTime CreatedAt, bool IsCurrent);
