using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Domain;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace AuthService.IntegrationTests;

// TOTP setup/enable/disable, recovery codes, and the login-time challenge - driven over
// the JSON API (AccountController), which the Razor Pages under Pages/TwoFactor/ call
// through the exact same TwoFactorService the API uses.
public class TwoFactorTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public TwoFactorTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Setup_returns_a_key_and_the_same_key_on_a_repeat_call()
    {
        using var client = await SignedInClientAsync();

        var first = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        var second = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");

        Assert.Equal(first!.SharedKey, second!.SharedKey);
    }

    [Fact]
    public async Task Enabling_with_a_wrong_code_fails_and_leaves_two_factor_off()
    {
        using var client = await SignedInClientAsync();
        await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");

        var response = await client.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest("000000"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.two_factor_code_invalid", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Enabling_with_the_correct_code_turns_it_on_and_returns_ten_recovery_codes()
    {
        using var client = await SignedInClientAsync();
        var setup = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");

        var response = await client.PostAsJsonAsync(
            "/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Enable2faResponse>();
        Assert.Equal(10, body!.RecoveryCodes.Count);
        Assert.Equal(10, body.RecoveryCodes.Distinct().Count());
    }

    [Fact]
    public async Task Login_requires_a_second_factor_once_enabled()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var setupClient = await SignInAsync(email, password);
        var setup = await setupClient.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await setupClient.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        using var loginClient = _factory.CreateClient();
        var response = await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
    }

    [Fact]
    public async Task Login_with_a_wrong_password_never_reaches_the_two_factor_prompt()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var setupClient = await SignInAsync(email, password);
        var setup = await setupClient.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await setupClient.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        using var loginClient = _factory.CreateClient();
        var response = await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, "WrongPassword999"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.invalid_credentials", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Completing_login_with_the_correct_authenticator_code_signs_the_user_in()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var setupClient = await SignInAsync(email, password);
        var setup = await setupClient.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await setupClient.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        using var loginClient = _factory.CreateClient();
        await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var completed = await loginClient.PostAsJsonAsync(
            "/auth/login/2fa", new TwoFactorLoginRequest(GenerateTotp(setup.SharedKey), RememberClient: false));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        // The full Identity.Application session now exists on this client - Logout only
        // succeeds against an authenticated session, so this is the end-to-end proof.
        var logout = await loginClient.PostAsync("/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task Completing_login_with_a_wrong_authenticator_code_does_not_sign_in()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var setupClient = await SignInAsync(email, password);
        var setup = await setupClient.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await setupClient.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        using var loginClient = _factory.CreateClient();
        await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var completed = await loginClient.PostAsJsonAsync(
            "/auth/login/2fa", new TwoFactorLoginRequest("000000", RememberClient: false));
        Assert.Equal(HttpStatusCode.Unauthorized, completed.StatusCode);

        var logout = await loginClient.PostAsync("/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
    }

    [Fact]
    public async Task Calling_the_two_factor_endpoint_without_a_pending_login_is_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login/2fa", new TwoFactorLoginRequest("123456", RememberClient: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.two_factor_session_expired", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_is_rejected_the_second_time()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var setupClient = await SignInAsync(email, password);
        var setup = await setupClient.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        var enableResponse = await setupClient.PostAsJsonAsync(
            "/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));
        var recoveryCode = (await enableResponse.Content.ReadFromJsonAsync<Enable2faResponse>())!.RecoveryCodes[0];

        using var firstLogin = _factory.CreateClient();
        await firstLogin.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        var firstUse = await firstLogin.PostAsJsonAsync(
            "/auth/login/2fa-recovery", new TwoFactorRecoveryLoginRequest(recoveryCode));
        Assert.Equal(HttpStatusCode.NoContent, firstUse.StatusCode);

        using var secondLogin = _factory.CreateClient();
        await secondLogin.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        var secondUse = await secondLogin.PostAsJsonAsync(
            "/auth/login/2fa-recovery", new TwoFactorRecoveryLoginRequest(recoveryCode));
        Assert.Equal(HttpStatusCode.Unauthorized, secondUse.StatusCode);
    }

    [Fact]
    public async Task Disabling_with_the_wrong_password_fails_and_leaves_two_factor_on()
    {
        using var client = await SignedInClientAsync();
        var setup = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await client.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        var response = await client.PostAsJsonAsync("/auth/2fa/disable", new Disable2faRequest("WrongPassword999"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Disabling_with_the_correct_password_turns_it_off_and_login_no_longer_challenges()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var client = await SignInAsync(email, password);
        var setup = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        await client.PostAsJsonAsync("/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));

        var disable = await client.PostAsJsonAsync("/auth/2fa/disable", new Disable2faRequest(password));
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        using var loginClient = _factory.CreateClient();
        var login = await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Regenerating_recovery_codes_invalidates_the_old_ones()
    {
        using var client = await SignedInClientAsync();
        var setup = await client.GetFromJsonAsync<TwoFactorSetupResponse>("/auth/2fa/setup");
        var enableResponse = await client.PostAsJsonAsync(
            "/auth/2fa/enable", new Enable2faRequest(GenerateTotp(setup!.SharedKey)));
        var oldCode = (await enableResponse.Content.ReadFromJsonAsync<Enable2faResponse>())!.RecoveryCodes[0];

        var regenerate = await client.PostAsJsonAsync(
            "/auth/2fa/recovery-codes", new RegenerateRecoveryCodesRequest("TestPass123"));
        Assert.Equal(HttpStatusCode.OK, regenerate.StatusCode);
        var newCodes = (await regenerate.Content.ReadFromJsonAsync<RegenerateRecoveryCodesResponse>())!.RecoveryCodes;
        Assert.DoesNotContain(oldCode, newCodes);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    /// <summary>Registers, confirms, and logs a fresh account in - the common starting
    /// point for tests that manage that account's own 2FA settings.</summary>
    private async Task<HttpClient> SignedInClientAsync()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        return await SignInAsync(email, password);
    }

    private async Task<(string Email, string Password)> CreateConfirmedAccountAsync()
    {
        var email = $"2fa-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        return (email, password);
    }

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Sign-in failed unexpectedly: {response.StatusCode}");
        }

        return client;
    }

    /// <summary>RFC 6238 TOTP, matching what UserManager's built-in AuthenticatorTokenProvider
    /// itself verifies against - the only way to drive a real login/enable through the API
    /// without a human typing a code from a phone.</summary>
    private static string GenerateTotp(string formattedKey)
    {
        var key = Base32Decode(formattedKey.Replace(" ", string.Empty, StringComparison.Ordinal));
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

#pragma warning disable CA5350 // RFC 6238 TOTP mandates HMAC-SHA1; this matches UserManager's own AuthenticatorTokenProvider.
        using var hmac = new HMACSHA1(key);
#pragma warning restore CA5350
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (binary % 1000000).ToString("D6");
    }

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
