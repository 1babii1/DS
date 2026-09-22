using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OpenIddict.Validation;

namespace AuthService.Web.Configuration;

/// <summary>
/// Owns the DB-backed signing/encryption key set OpenIddict's server options are built from
/// (see AuthenticationConfiguration.AddDynamicSigningKeys) and the rotation policy itself.
/// Multiple active keys of the same purpose can coexist by design - see SigningKeyRecord and
/// SigningKeyRotationJob for why signing and encryption keys need different retention windows.
/// </summary>
public class SigningKeyStore(
    AuthDbContext dbContext,
    SigningKeyProtector protector,
    IOptionsMonitorCache<OpenIddictServerOptions> serverOptionsCache,
    IOptionsMonitorCache<OpenIddictValidationOptions> validationOptionsCache)
{
    /// <summary>How often a new primary key is minted, for both purposes - the two purposes
    /// differ in how long a *retired* key must keep validating, not in how often rotation
    /// itself happens.</summary>
    public static readonly TimeSpan RotationInterval = TimeSpan.FromDays(30);

    /// <summary>Signing keys back short-lived (15-minute) access tokens - a couple of days
    /// past being superseded is generous slack for tokens issued right at the boundary plus
    /// any JWKS caching delay on the resource services validating them.</summary>
    public static readonly TimeSpan SigningKeyRetention = TimeSpan.FromDays(2);

    /// <summary>Encryption keys protect refresh tokens, which live up to 30 days - retiring
    /// one any sooner would silently break every refresh token issued under it while it was
    /// still current.</summary>
    public static readonly TimeSpan EncryptionKeyRetention = TimeSpan.FromDays(35);

    /// <summary>Arbitrary constant naming this service's key-management critical section for
    /// <c>pg_advisory_xact_lock</c>. Any two instances that touch the key set (a rolling deploy
    /// overlapping old and new, a second replica, a startup racing a scheduled run) queue on it
    /// instead of each minting their own new key.</summary>
    private const long KeyManagementLockId = 7_303_017_432_118_001;

    public async Task<IReadOnlyList<SigningKeyRecord>> GetActiveKeysAsync(
        SigningKeyPurpose purpose, CancellationToken cancellationToken) =>
        await dbContext.SigningKeys
            .AsNoTracking()
            .Where(k => k.Purpose == purpose && k.RetiredAt == null)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Bootstrap path for a brand-new deployment: mints an initial key for any
    /// purpose that has none yet. A no-op past the very first startup. Serialized with rotation
    /// so two instances starting together do not each mint a first key.</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
        await RunExclusivelyAsync(
            async () =>
            {
                foreach (var purpose in new[] { SigningKeyPurpose.Signing, SigningKeyPurpose.Encryption })
                {
                    var hasActiveKey = await dbContext.SigningKeys
                        .AnyAsync(k => k.Purpose == purpose && k.RetiredAt == null, cancellationToken);
                    if (!hasActiveKey)
                    {
                        await GenerateKeyAsync(purpose, cancellationToken);
                    }
                }

                return true;
            },
            cancellationToken);

    /// <summary>Encrypts any private key still stored as plaintext (rows written before
    /// <c>SigningKeys:AtRestKeyBase64</c> was configured). A no-op without a master key or when
    /// everything is already protected.</summary>
    public async Task ProtectLegacyKeysAsync(CancellationToken cancellationToken)
    {
        if (!protector.IsEnabled)
        {
            return;
        }

        await RunExclusivelyAsync(
            async () =>
            {
                var legacy = await dbContext.SigningKeys
                    .Where(k => !k.PrivateKeyPem.StartsWith(SigningKeyProtector.Prefix))
                    .ToListAsync(cancellationToken);
                foreach (var key in legacy)
                {
                    key.PrivateKeyPem = protector.Protect(key.PrivateKeyPem, key.Purpose, key.KeyId);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            },
            cancellationToken);
    }

    /// <returns>True if a rotation actually happened - false when the current primary key
    /// has not yet aged past RotationInterval, so a daily check is a no-op most days.
    /// The due-check and the mint happen under one advisory lock and the check is re-read after
    /// the lock is held, so concurrent callers produce exactly one new key: the loser waits, then
    /// sees the winner's fresh primary and does nothing.</returns>
    public async Task<bool> RotateIfDueAsync(SigningKeyPurpose purpose, CancellationToken cancellationToken)
    {
        var rotated = await RunExclusivelyAsync(
            async () =>
            {
                var active = await GetActiveKeysAsync(purpose, cancellationToken);
                var primary = active.LastOrDefault();
                if (primary is not null && DateTime.UtcNow - primary.CreatedAt < RotationInterval)
                {
                    return false;
                }

                await GenerateKeyAsync(purpose, cancellationToken);

                // Retention counts from when a key would typically have been *superseded*, not from
                // its own creation date - a key is only superseded once it stops being primary, which
                // (assuming rotation stays roughly on schedule) is RotationInterval after it was
                // created. Cutting off at "CreatedAt < now - retention" alone would retire a 30-day-old
                // signing key immediately on the very rotation that supersedes it, giving it none of
                // its intended 2-day grace period rather than 2 days *after* being superseded.
                var retention = purpose == SigningKeyPurpose.Encryption ? EncryptionKeyRetention : SigningKeyRetention;
                var cutoff = DateTime.UtcNow - RotationInterval - retention;
                var retiredAt = DateTime.UtcNow;
                var expiredIds = active.Where(k => k.CreatedAt < cutoff).Select(k => k.Id).ToList();
                if (expiredIds.Count > 0)
                {
                    await dbContext.SigningKeys
                        .Where(k => expiredIds.Contains(k.Id))
                        .ExecuteUpdateAsync(s => s.SetProperty(k => k.RetiredAt, (DateTime?)retiredAt), cancellationToken);
                }

                return true;
            },
            cancellationToken);

        if (rotated)
        {
            InvalidateOpenIddictOptions();
        }

        return rotated;
    }

    /// <summary>OpenIddictServerDispatcher/Factory resolve OpenIddictServerOptions through
    /// IOptionsMonitor&lt;T&gt;, not IOptions&lt;T&gt; - confirmed directly via reflection
    /// rather than assumed, since a rotation that silently needs an app restart to take
    /// effect would defeat the entire point of automating it. Clearing the cache forces the
    /// Configure callback in AuthenticationConfiguration to re-run and read the new key set
    /// on the very next request - no restart.</summary>
    /// <summary>Both OpenIddictServerOptions (token issuance, e.g. /connect/token) and
    /// OpenIddictValidationOptions (token validation, e.g. /connect/userinfo and every
    /// [Authorize]-protected endpoint here) cache their own derived key material separately
    /// - UseLocalServer() does not mean the validation side automatically re-reads the
    /// server side's options on invalidation, confirmed the hard way when a freshly-rotated
    /// token could be issued but not validated until this second cache was also cleared.</summary>
    public void InvalidateOpenIddictOptions()
    {
        serverOptionsCache.TryRemove(Microsoft.Extensions.Options.Options.DefaultName);
        validationOptionsCache.TryRemove(Microsoft.Extensions.Options.Options.DefaultName);
    }

    private async Task<T> RunExclusivelyAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({KeyManagementLockId})", cancellationToken);
        var result = await action();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task GenerateKeyAsync(SigningKeyPurpose purpose, CancellationToken cancellationToken)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var keyId = Guid.NewGuid().ToString("N");
        dbContext.SigningKeys.Add(new SigningKeyRecord
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            KeyId = keyId,
            PrivateKeyPem = protector.Protect(rsa.ExportPkcs8PrivateKeyPem(), purpose, keyId),
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public RsaSecurityKey ImportKey(SigningKeyRecord record)
    {
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportFromPem(protector.Unprotect(record.PrivateKeyPem, record.Purpose, record.KeyId));
        return new RsaSecurityKey(rsa) { KeyId = record.KeyId };
    }
}
