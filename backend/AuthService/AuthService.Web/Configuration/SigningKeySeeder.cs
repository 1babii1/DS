using System.Text;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Web.Configuration;

/// <summary>
/// Runs once at startup, before the first request can trigger OpenIddict's own
/// IOptionsMonitor&lt;OpenIddictServerOptions&gt; resolution (see AddDynamicSigningKeys) -
/// without at least one active signing and one active encryption key already in the
/// database by then, the server would have nothing to sign a token with. An
/// already-deployed instance's SigningKeys__SigningKeyBase64/EncryptionKeyBase64 env vars
/// (the old static-key mechanism) are imported as that first key if the table is still
/// empty, so upgrading to key rotation does not invalidate every session already issued
/// under them; a fresh deployment with neither just generates its own.
/// </summary>
public static class SigningKeySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var protector = scope.ServiceProvider.GetRequiredService<SigningKeyProtector>();
        if (!protector.IsEnabled)
        {
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SigningKeySeeder)).LogWarning(
                "{Setting} is not set: token signing/encryption private keys are stored in the database as plaintext. Acceptable for local development only.",
                SigningKeyProtector.ConfigurationKey);
        }

        await ImportIfConfiguredAndEmptyAsync(
            dbContext, protector, SigningKeyPurpose.Signing, configuration["SigningKeys:SigningKeyBase64"]);
        await ImportIfConfiguredAndEmptyAsync(
            dbContext, protector, SigningKeyPurpose.Encryption, configuration["SigningKeys:EncryptionKeyBase64"]);
        await dbContext.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
        await store.EnsureInitializedAsync(CancellationToken.None);
        await store.ProtectLegacyKeysAsync(CancellationToken.None);

        // Belt-and-braces: if anything resolved OpenIddictServerOptions before this seeder
        // ran (a real, observed failure - "At least one encryption key must be registered"
        // under test-suite load, where something touches the options early enough to cache
        // an empty key list), that empty result would otherwise stay cached forever, since
        // nothing else invalidates it until the first rotation. Clearing it here guarantees
        // the very first real resolution sees the keys this seeder just wrote, regardless of
        // what may have touched the options before it ran.
        store.InvalidateOpenIddictOptions();
    }

    private static async Task ImportIfConfiguredAndEmptyAsync(
        AuthDbContext dbContext, SigningKeyProtector protector, SigningKeyPurpose purpose, string? base64Pem)
    {
        if (string.IsNullOrWhiteSpace(base64Pem))
        {
            return;
        }

        var hasActiveKey = await dbContext.SigningKeys.AnyAsync(k => k.Purpose == purpose && k.RetiredAt == null);
        if (hasActiveKey)
        {
            return;
        }

        var pem = Encoding.UTF8.GetString(Convert.FromBase64String(base64Pem));
        var keyId = Guid.NewGuid().ToString("N");
        dbContext.SigningKeys.Add(new SigningKeyRecord
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            KeyId = keyId,
            PrivateKeyPem = protector.Protect(pem, purpose, keyId),
            CreatedAt = DateTime.UtcNow,
        });
    }
}
