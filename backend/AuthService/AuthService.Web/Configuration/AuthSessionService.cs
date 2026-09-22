using System.Security.Claims;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Web.Configuration;

/// <summary>
/// Owns the "active sessions" list GitHub/Google-style account settings pages show, backed
/// by AuthSession rows rather than OpenIddict's own authorizations - those are keyed per
/// OAuth client and can outlive or split across what a person thinks of as "the device I'm
/// signed in on right now". See AuthSession's own doc comment for the full reasoning, and
/// AuthenticationConfiguration for the per-request revocation check this enables.
/// </summary>
public class AuthSessionService(AuthDbContext dbContext, SignInManager<Account> signInManager)
{
    public const string SessionIdClaimType = "session_id";

    /// <summary>
    /// Called once, right after any of this project's several sign-in paths (password, 2FA,
    /// passkey, external login) already established the Identity.Application cookie -
    /// re-issues that same cookie with an added session_id claim rather than threading the
    /// claim through each of those very different SignInManager call shapes, so this feature
    /// cannot regress any of their already-tested lockout/2FA/account-linking behavior.
    /// isPersistent is read back from the cookie those call sites already wrote (some pass it
    /// explicitly, one - TwoFactorRecoveryCodeSignInAsync - does not even expose it as a
    /// parameter) rather than asked of the caller, so this can never re-derive it wrong.
    /// </summary>
    public async Task EstablishAsync(Account user, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        var authenticateResult = await signInManager.Context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var isPersistent = authenticateResult.Properties?.IsPersistent ?? false;

        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            AccountId = user.Id,
            IpAddress = ipAddress,
            UserAgent = userAgent is null ? null : userAgent[..Math.Min(userAgent.Length, 500)],
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.AuthSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        await signInManager.SignInWithClaimsAsync(
            user, isPersistent, [new Claim(SessionIdClaimType, session.Id.ToString())]);
    }

    public async Task<IReadOnlyList<AuthSessionSummary>> ListAsync(
        Guid accountId, Guid? currentSessionId, CancellationToken cancellationToken) =>
        await dbContext.AuthSessions
            .Where(s => s.AccountId == accountId && s.RevokedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new AuthSessionSummary(s.Id, s.IpAddress, s.UserAgent, s.CreatedAt, s.Id == currentSessionId))
            .ToListAsync(cancellationToken);

    /// <returns>False if no matching, still-active session belongs to this account - scoped
    /// by AccountId so one account can never revoke another's session by guessing an id.</returns>
    public async Task<bool> RevokeAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.AuthSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.AccountId == accountId && s.RevokedAt == null, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.RevokedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Backs AccountRecoveryService.RevokeAllSessionsAsync - marking every one of
    /// this account's sessions revoked is what makes a *different* browser's cookie actually
    /// stop working on its own next request, on top of the current request's explicit
    /// SignOutAsync and the security-stamp rotation that only takes effect on its own
    /// interval elsewhere. Without this, "sign out everywhere" would leave every
    /// session_id-carrying cookie except the current one still valid.</summary>
    public async Task RevokeAllAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await dbContext.AuthSessions
            .Where(s => s.AccountId == accountId && s.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, DateTime.UtcNow), cancellationToken);
    }

    /// <summary>What AuthenticationConfiguration's OnValidatePrincipal hook checks on every
    /// request - a session missing entirely (issued before this feature shipped, or by a
    /// code path that does not call EstablishAsync) is treated as still valid, not revoked;
    /// this only ever narrows validity, never grants it.</summary>
    public async Task<bool> IsValidAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await dbContext.AuthSessions.AnyAsync(s => s.Id == sessionId && s.RevokedAt == null, cancellationToken);
}
