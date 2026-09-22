namespace AuthService.Domain;

/// <summary>
/// One browser-level sign-in (Identity.Application cookie), tracked separately from
/// OpenIddict's own authorizations/tokens because those are keyed per OAuth client, not per
/// device - a single sign-in here can back several different OAuth clients' tokens, and
/// "sessions" as GitHub/Google mean it is this browser-cookie concept, not a token concept.
/// Its Id is embedded as a claim in the cookie itself (AuthSessionService), and checked
/// against RevokedAt on every request via a custom OnValidatePrincipal hook chained after
/// Identity's own security-stamp check - see AuthenticationConfiguration.
/// </summary>
public class AuthSession
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
