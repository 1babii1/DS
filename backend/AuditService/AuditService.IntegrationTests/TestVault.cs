using AuditService.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuditService.IntegrationTests;

internal static class TestVault
{
    /// <summary>A vault with no master key configured: payloads pass through untouched, as before ADR 0046.</summary>
    public static PiiVault Disabled(AuditDbContext db) =>
        new(db, Options.Create(new PiiVaultOptions()), NullLogger<PiiVault>.Instance);

    /// <summary>A vault with a master key, over the same database.</summary>
    public static PiiVault Enabled(AuditDbContext db, byte seed = 7) =>
        new(db, Options.Create(new PiiVaultOptions { PiiMasterKeyBase64 = Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray()) }), NullLogger<PiiVault>.Instance);
}
