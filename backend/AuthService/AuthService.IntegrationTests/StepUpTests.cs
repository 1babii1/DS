using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Shared;
using Shared.Security;

namespace AuthService.IntegrationTests;

// GitHub-style "sudo mode": step-up/status, step-up/verify (authenticator and email-code
// paths), and the elevated_until claim only appearing on a token minted *after* verification
// - driven through a real PKCE authorization_code -> refresh_token exchange, the same
// end-to-end shape AccountRecoveryTests uses for its own refresh-token assertions, since the
// claim only exists on actual OpenIddict-issued access tokens, not anything this test could
// construct by hand.
public class StepUpTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private const string RedirectUri = "http://localhost:3000/auth/callback";

    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public StepUpTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Status_reports_the_email_fallback_for_an_account_without_two_factor()
    {
        var (client, _) = await SignedInClientAsync();
        using var clientDisposer = client;

        var status = await client.GetFromJsonAsync<StepUpStatusResponse>("/auth/step-up/status");

        Assert.False(status!.TwoFactorEnabled);
        Assert.Null(status.ElevatedUntil);
    }

    [Fact]
    public async Task Requesting_an_email_code_is_rejected_once_two_factor_is_enabled()
    {
        var (client, _) = await SignedInClientAsync();
        using var clientDisposer = client;
        await EnableTwoFactorAsync(client);

        var response = await client.PostAsync("/auth/step-up/request-email-code", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.step_up_email_not_applicable", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task A_wrong_email_code_does_not_elevate_the_account()
    {
        var (client, _) = await SignedInClientAsync();
        using var clientDisposer = client;
        await client.PostAsync("/auth/step-up/request-email-code", content: null);

        var response = await client.PostAsJsonAsync("/auth/step-up/verify", new StepUpVerifyRequest("000000"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await client.GetFromJsonAsync<StepUpStatusResponse>("/auth/step-up/status");
        Assert.Null(status!.ElevatedUntil);
    }

    [Fact]
    public async Task A_correct_email_code_elevates_the_account_but_only_a_refreshed_token_carries_the_claim()
    {
        var email = $"stepup-{Guid.NewGuid():N}@test.local";
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

        var preElevationTokens = await ExchangeAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = "portfolio-frontend",
            ["code_verifier"] = verifier,
        });
        var accessTokenBeforeStepUp = preElevationTokens.GetProperty("access_token").GetString()!;
        var refreshToken = preElevationTokens.GetProperty("refresh_token").GetString()!;

        Assert.False(DecodeJwtPayload(accessTokenBeforeStepUp).TryGetProperty(
            StepUpClaims.ElevatedUntilClaim, out _));

        await client.PostAsync("/auth/step-up/request-email-code", content: null);
        var emailedCode = _factory.EmailSender.StepUpCodes.Last(c => c.ToEmail == email).Code;

        var verifyResponse = await client.PostAsJsonAsync("/auth/step-up/verify", new StepUpVerifyRequest(emailedCode));
        Assert.Equal(HttpStatusCode.NoContent, verifyResponse.StatusCode);

        var status = await client.GetFromJsonAsync<StepUpStatusResponse>("/auth/step-up/status");
        Assert.NotNull(status!.ElevatedUntil);
        Assert.True(status.ElevatedUntil > DateTime.UtcNow);

        var refreshedTokens = await ExchangeAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "portfolio-frontend",
        });
        var accessTokenAfterStepUp = refreshedTokens.GetProperty("access_token").GetString()!;

        var claim = DecodeJwtPayload(accessTokenAfterStepUp).GetProperty(StepUpClaims.ElevatedUntilClaim).GetString();
        var elevatedUntilFromToken = DateTimeOffset.FromUnixTimeSeconds(long.Parse(claim!));
        Assert.True(elevatedUntilFromToken > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_correct_authenticator_code_elevates_a_two_factor_enabled_account()
    {
        var (client, _) = await SignedInClientAsync();
        using var clientDisposer = client;
        var setup = await EnableTwoFactorAsync(client);

        var response = await client.PostAsJsonAsync(
            "/auth/step-up/verify", new StepUpVerifyRequest(GenerateTotp(setup)));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var status = await client.GetFromJsonAsync<StepUpStatusResponse>("/auth/step-up/status");
        Assert.NotNull(status!.ElevatedUntil);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(HttpClient Client, string Email)> SignedInClientAsync()
    {
        var email = $"stepup-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Sign-in failed unexpectedly: {response.StatusCode}");
        }

        return (client, email);
    }

    private async Task<string> EnableTwoFactorAsync(HttpClient client)
    {
        var setup = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await client.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));
        return setup.SharedKey;
    }

    private static async Task<JsonElement> ExchangeAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1];
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
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

#pragma warning disable CA5350 // RFC 6238 TOTP mandates HMAC-SHA1; matches UserManager's own AuthenticatorTokenProvider.
    private static string GenerateTotp(string formattedKey)
    {
        var key = Base32Decode(formattedKey.Replace(" ", string.Empty, StringComparison.Ordinal));
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (binary % 1000000).ToString("D6");
    }
#pragma warning restore CA5350

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=');
        var bits = new List<bool>();
        foreach (var c in input)
        {
            var value = alphabet.IndexOf(char.ToUpperInvariant(c));
            for (var i = 4; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) == 1);
            }
        }

        var bytes = new byte[bits.Count / 8];
        for (var i = 0; i < bytes.Length; i++)
        {
            byte b = 0;
            for (var j = 0; j < 8; j++)
            {
                if (bits[(i * 8) + j])
                {
                    b |= (byte)(1 << (7 - j));
                }
            }

            bytes[i] = b;
        }

        return bytes;
    }
}
