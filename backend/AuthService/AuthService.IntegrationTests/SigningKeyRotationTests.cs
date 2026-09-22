using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace AuthService.IntegrationTests;

// Proves the actual point of automatic key rotation, not just that SigningKeyStore's own
// bookkeeping is internally consistent: a token issued before a rotation keeps validating
// after it (the old key's grace window), a token issued after it carries a *different* kid
// (proving OpenIddict actually picked up the new key, and picked the new one to sign with -
// verified here rather than assumed, since getting the "which key signs new tokens" ordering
// backward would make rotation permanently sign with the oldest key forever), and
// IOptionsMonitorCache invalidation genuinely takes effect on the very next request with no
// app restart - confirmed directly via reflection earlier (OpenIddictServerDispatcher/
// Factory resolve options through IOptionsMonitor<T>, not IOptions<T>), proven here for real.
public class SigningKeyRotationTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private const string RedirectUri = "http://localhost:3000/auth/callback";

    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public SigningKeyRotationTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task The_bootstrap_seeder_leaves_exactly_one_active_key_per_purpose()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();

        var signingKeys = await store.GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None);
        var encryptionKeys = await store.GetActiveKeysAsync(SigningKeyPurpose.Encryption, CancellationToken.None);

        Assert.Single(signingKeys);
        Assert.Single(encryptionKeys);
    }

    [Fact]
    public async Task Rotation_is_a_noop_while_the_current_key_is_still_fresh()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();

        var rotated = await store.RotateIfDueAsync(SigningKeyPurpose.Signing, CancellationToken.None);

        Assert.False(rotated);
        Assert.Single(await store.GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None));
    }

    [Fact]
    public async Task Rotation_mints_a_new_key_once_the_current_one_is_past_the_rotation_interval()
    {
        await BackdatePrimaryKeyAsync(SigningKeyPurpose.Signing, SigningKeyStore.RotationInterval + TimeSpan.FromDays(1));

        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
        var rotated = await store.RotateIfDueAsync(SigningKeyPurpose.Signing, CancellationToken.None);

        Assert.True(rotated);

        // Both keys are still active: the old one is only 1 day past being superseded,
        // comfortably inside SigningKeyRetention's 2-day grace window.
        Assert.Equal(2, (await store.GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_key_far_past_its_retention_window_is_retired_on_the_next_rotation()
    {
        // Rotation interval (30d) + signing retention (2d) + a further margin: old enough
        // that the grace period granted at the moment it's superseded has also elapsed.
        await BackdatePrimaryKeyAsync(
            SigningKeyPurpose.Signing, SigningKeyStore.RotationInterval + SigningKeyStore.SigningKeyRetention + TimeSpan.FromDays(1));

        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
        await store.RotateIfDueAsync(SigningKeyPurpose.Signing, CancellationToken.None);

        var active = await store.GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None);
        Assert.Single(active);
    }

    [Fact]
    public async Task A_token_issued_before_rotation_keeps_validating_after_it_while_a_new_token_uses_a_different_key()
    {
        var beforeToken = await GetAccessTokenAsync();
        var beforeKid = DecodeJwtHeader(beforeToken).GetProperty("kid").GetString();

        // Confirms the pre-rotation token actually authenticates before touching anything -
        // otherwise a later failure here couldn't tell "rotation broke it" apart from "it
        // never worked to begin with".
        Assert.Equal(System.Net.HttpStatusCode.OK, await CallUserinfoAsync(beforeToken));

        await BackdatePrimaryKeyAsync(SigningKeyPurpose.Signing, SigningKeyStore.RotationInterval + TimeSpan.FromDays(1));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
            var rotated = await store.RotateIfDueAsync(SigningKeyPurpose.Signing, CancellationToken.None);
            Assert.True(rotated);
        }

        var afterToken = await GetAccessTokenAsync();
        var afterKid = DecodeJwtHeader(afterToken).GetProperty("kid").GetString();

        Assert.NotEqual(beforeKid, afterKid);
        Assert.Equal(System.Net.HttpStatusCode.OK, await CallUserinfoAsync(afterToken));

        // The whole point: no restart between the two calls above, and the pre-rotation
        // token - signed by the now-superseded key - still authenticates.
        Assert.Equal(System.Net.HttpStatusCode.OK, await CallUserinfoAsync(beforeToken));
    }

    [Fact]
    public async Task Private_keys_are_stored_encrypted_and_still_sign_and_validate_tokens()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var rows = await dbContext.SigningKeys.AsNoTracking().ToListAsync();

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.StartsWith(SigningKeyProtector.Prefix, row.PrivateKeyPem, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", row.PrivateKeyPem, StringComparison.Ordinal);
        });

        // The end-to-end proof: keys read back from those encrypted rows actually issue and
        // validate a token (the whole suite runs with at-rest encryption on).
        var token = await GetAccessTokenAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, await CallUserinfoAsync(token));
    }

    [Fact]
    public async Task Legacy_plaintext_keys_are_encrypted_in_place_and_keep_working()
    {
        string plaintextPem;
        Guid legacyId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            using var rsa = RSA.Create(2048);
            plaintextPem = rsa.ExportPkcs8PrivateKeyPem();
            var legacy = new SigningKeyRecord
            {
                Id = Guid.NewGuid(),
                Purpose = SigningKeyPurpose.Signing,
                KeyId = "legacy-plaintext",
                PrivateKeyPem = plaintextPem,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            };
            legacyId = legacy.Id;
            dbContext.SigningKeys.Add(legacy);
            await dbContext.SaveChangesAsync();
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SigningKeyStore>().ProtectLegacyKeysAsync(CancellationToken.None);
        }

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var row = await verifyScope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .SigningKeys.AsNoTracking().SingleAsync(k => k.Id == legacyId);
        Assert.StartsWith(SigningKeyProtector.Prefix, row.PrivateKeyPem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", row.PrivateKeyPem, StringComparison.Ordinal);

        var imported = verifyScope.ServiceProvider.GetRequiredService<SigningKeyStore>().ImportKey(row);
        using var expected = RSA.Create();
        expected.ImportFromPem(plaintextPem);
        Assert.Equal(expected.ExportParameters(false).Modulus, imported.Rsa!.ExportParameters(false).Modulus);
    }

    [Fact]
    public async Task Concurrent_rotation_attempts_mint_exactly_one_new_key()
    {
        // Two instances (a rolling deploy, a second replica, startup racing the daily job) must not
        // each mint a key. Eight callers in separate scopes = separate DbContexts and connections,
        // all seeing the same stale primary; RSA generation is slow enough that without the
        // database-level lock they overlap for certain.
        await BackdatePrimaryKeyAsync(SigningKeyPurpose.Signing, SigningKeyStore.RotationInterval + TimeSpan.FromDays(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
            return await store.RotateIfDueAsync(SigningKeyPurpose.Signing, CancellationToken.None);
        }));

        Assert.Equal(1, results.Count(rotated => rotated));
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var active = await verifyScope.ServiceProvider.GetRequiredService<SigningKeyStore>()
            .GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None);
        Assert.Equal(2, active.Count);
    }

    [Fact]
    public async Task Concurrent_first_time_initialization_mints_exactly_one_key_per_purpose()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            dbContext.SigningKeys.RemoveRange(dbContext.SigningKeys);
            await dbContext.SaveChangesAsync();
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SigningKeyStore>().EnsureInitializedAsync(CancellationToken.None);
        }));

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var store = verifyScope.ServiceProvider.GetRequiredService<SigningKeyStore>();
        Assert.Single(await store.GetActiveKeysAsync(SigningKeyPurpose.Signing, CancellationToken.None));
        Assert.Single(await store.GetActiveKeysAsync(SigningKeyPurpose.Encryption, CancellationToken.None));
    }

    // signing_keys is deliberately excluded from AuthTestWebFactory's Respawn reset (it's
    // shared bootstrap state for every other test class) - but this class actively rotates
    // and backdates it as its own test subject, so each test needs its own clean single-key
    // baseline rather than inheriting whatever a previous test in this class left behind.
    public async Task InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        dbContext.SigningKeys.RemoveRange(dbContext.SigningKeys);
        await dbContext.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<SigningKeyStore>();
        await store.EnsureInitializedAsync(CancellationToken.None);
        store.InvalidateOpenIddictOptions();
    }

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task BackdatePrimaryKeyAsync(SigningKeyPurpose purpose, TimeSpan age)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var key = await dbContext.SigningKeys.SingleAsync(k => k.Purpose == purpose && k.RetiredAt == null);
        key.CreatedAt = DateTime.UtcNow - age;
        await dbContext.SaveChangesAsync();
    }

    private async Task<string> GetAccessTokenAsync()
    {
        var email = $"rotation-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var (verifier, challenge) = GeneratePkcePair();
        var authorizeUrl = "/connect/authorize" +
            $"?client_id=portfolio-frontend&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid offline_access") +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=x";
        var authorizeResponse = await client.GetAsync(authorizeUrl);
        var code = System.Web.HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]!;

        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = "portfolio-frontend",
            ["code_verifier"] = verifier,
        }));
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return tokens.GetProperty("access_token").GetString()!;
    }

    private async Task<System.Net.HttpStatusCode> CallUserinfoAsync(string accessToken)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.GetAsync("/connect/userinfo");
        return response.StatusCode;
    }

    private static JsonElement DecodeJwtHeader(string jwt)
    {
        var header = jwt.Split('.')[0];
        var padded = header.PadRight(header.Length + ((4 - (header.Length % 4)) % 4), '=');
        var bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
