namespace AuthService.Application;

public class AuthOptions
{
    public const string SectionName = "Auth";

    public required string Issuer { get; init; }

    public required SeedAdminOptions SeedAdmin { get; init; }
}

public class SeedAdminOptions
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}