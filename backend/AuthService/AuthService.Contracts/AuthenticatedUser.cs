namespace AuthService.Contracts;

public record AuthenticatedUser(Guid Id, string Email, IReadOnlyCollection<string> Roles);