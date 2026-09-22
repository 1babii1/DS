using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Domain;
using AuthService.Web.Configuration;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared;

namespace AuthService.IntegrationTests;

// Email confirmation, resend, forgot-password and reset-password - the self-service
// flows AccountRecoveryService backs for both AccountController and the Password/Email
// Razor Pages. Driven over the JSON API (AccountController), since that is what the two
// entry points share; the Razor Pages layer only adds form rendering on top and is not
// re-verified here.
public class AccountRecoveryTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public AccountRecoveryTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Confirming_with_an_invalid_token_does_not_confirm_the_account()
    {
        var email = $"confirm-bad-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));

        var response = await client.GetAsync($"/auth/email/confirm?email={Uri.EscapeDataString(email)}&token=not-a-real-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid or expired", body, StringComparison.OrdinalIgnoreCase);

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.False(user!.EmailConfirmed);
    }

    [Fact]
    public async Task Confirming_for_an_unknown_email_answers_the_same_way_as_a_bad_token()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/auth/email/confirm?email={Uri.EscapeDataString($"nobody-{Guid.NewGuid():N}@test.local")}&token=whatever");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid or expired", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Confirming_with_the_real_link_activates_the_account()
    {
        var email = $"confirm-ok-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        var link = _factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link;

        var response = await client.GetAsync(link);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.True((await userManager.FindByEmailAsync(email))!.EmailConfirmed);
    }

    [Fact]
    public async Task Confirming_the_same_link_twice_is_a_harmless_no_op()
    {
        // Unlike ResetPasswordAsync, ConfirmEmailAsync does not rotate the security
        // stamp - verified directly against UserManager rather than assumed by analogy,
        // since the two look like they should behave the same and do not. The token
        // stays valid for a second click, which is fine: setting EmailConfirmed = true
        // twice is idempotent, so a user who double-clicks (or a scanner that
        // pre-fetches email links) does not see a confusing failure.
        var email = $"confirm-replay-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        var link = _factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link;

        await client.GetAsync(link);
        var second = await client.GetAsync(link);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("Email confirmed", body, StringComparison.Ordinal);

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.True((await userManager.FindByEmailAsync(email))!.EmailConfirmed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resend_confirmation_always_answers_204_whether_or_not_it_actually_sends(bool accountExists)
    {
        var email = accountExists
            ? $"resend-{Guid.NewGuid():N}@test.local"
            : $"resend-missing-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        if (accountExists)
        {
            await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        }

        var before = _factory.EmailSender.Confirmations.Count(c => c.ToEmail == email);
        var response = await client.PostAsJsonAsync("/auth/resend-confirmation", new ResendConfirmationRequest(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = _factory.EmailSender.Confirmations.Count(c => c.ToEmail == email);
        Assert.Equal(accountExists ? before + 1 : before, after);
    }

    [Fact]
    public async Task Resend_confirmation_for_an_already_confirmed_account_does_not_send_again()
    {
        var email = $"resend-confirmed-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        var before = _factory.EmailSender.Confirmations.Count(c => c.ToEmail == email);

        var response = await client.PostAsJsonAsync("/auth/resend-confirmation", new ResendConfirmationRequest(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, _factory.EmailSender.Confirmations.Count(c => c.ToEmail == email));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forgot_password_always_answers_204_whether_or_not_the_account_exists(bool accountExists)
    {
        var email = accountExists
            ? $"forgot-{Guid.NewGuid():N}@test.local"
            : $"forgot-missing-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        if (accountExists)
        {
            await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        }

        var response = await client.PostAsJsonAsync("/auth/forgot-password", new ForgotPasswordRequest(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(accountExists, _factory.EmailSender.Resets.Any(r => r.ToEmail == email));
    }

    [Fact]
    public async Task Reset_password_with_a_valid_token_changes_the_password()
    {
        var email = $"reset-ok-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "OldPass123"));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        await client.PostAsJsonAsync("/auth/forgot-password", new ForgotPasswordRequest(email));
        var (_, link) = _factory.EmailSender.Resets.Last(r => r.ToEmail == email);
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;

        var reset = await client.PostAsJsonAsync(
            "/auth/reset-password", new ResetPasswordRequest(email, token, "NewPass456"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var loginWithOld = _factory.CreateClient();
        var oldLogin = await loginWithOld.PostAsJsonAsync("/auth/login", new LoginRequest(email, "OldPass123"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        using var loginWithNew = _factory.CreateClient();
        var newLogin = await loginWithNew.PostAsJsonAsync("/auth/login", new LoginRequest(email, "NewPass456"));
        Assert.Equal(HttpStatusCode.NoContent, newLogin.StatusCode);
    }

    [Fact]
    public async Task Reset_password_with_an_invalid_token_fails_generically()
    {
        var email = $"reset-bad-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));

        var response = await client.PostAsJsonAsync(
            "/auth/reset-password", new ResetPasswordRequest(email, "not-a-real-token", "NewPass456"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.reset_token_invalid", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Reset_password_for_an_unknown_email_fails_the_same_way_as_a_bad_token()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/reset-password",
            new ResetPasswordRequest($"nobody-{Guid.NewGuid():N}@test.local", "whatever", "NewPass456"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.reset_token_invalid", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Reset_password_revokes_a_refresh_token_issued_before_the_reset()
    {
        // Drives the real PKCE authorization_code -> refresh_token exchange rather than
        // hand-constructing an OpenIddictAuthorizationDescriptor: an earlier version of
        // this test created one directly with a guessed Type (Permanent), and
        // RevokeBySubjectAsync left it untouched - which only proved something about a
        // shape of authorization this app may never actually issue, not about whether the
        // real login flow's tokens get revoked. Testing the observable, user-facing
        // property (does a refresh token obtained before the reset still work after it)
        // is what actually matters here, and does not depend on knowing OpenIddict's
        // internal defaults.
        var email = $"reset-revoke-{Guid.NewGuid():N}@test.local";
        const string oldPassword = "OldPass123";
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, oldPassword));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        var signedIn = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, oldPassword));
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);

        var (verifier, challenge) = GeneratePkcePair();
        const string redirectUri = "http://localhost:3000/auth/callback";
        var authorizeUrl = "/connect/authorize" +
            $"?client_id=portfolio-frontend&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid offline_access") +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=x";
        var authorizeResponse = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorizeResponse.StatusCode);
        var code = System.Web.HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"];
        Assert.False(string.IsNullOrEmpty(code));

        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = "portfolio-frontend",
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var resetLinkRequest = await client.PostAsJsonAsync("/auth/forgot-password", new ForgotPasswordRequest(email));
        Assert.Equal(HttpStatusCode.NoContent, resetLinkRequest.StatusCode);
        var (_, link) = _factory.EmailSender.Resets.Last(r => r.ToEmail == email);
        var resetToken = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;
        var reset = await client.PostAsJsonAsync(
            "/auth/reset-password", new ResetPasswordRequest(email, resetToken, "NewPass456"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var refreshAttempt = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "portfolio-frontend",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, refreshAttempt.StatusCode);
    }

    private static (string Verifier, string Challenge) GeneratePkcePair()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Password_reset_tokens_use_a_shorter_lifespan_than_the_shared_default()
    {
        // Regression guard for a real wiring bug caught before this shipped:
        // DataProtectorTokenProvider<TUser> resolves a plain, unnamed
        // IOptions<DataProtectionTokenProviderOptions> - registering a second provider
        // under a different *name* alone does not give it its own lifespan, because every
        // instance of that same concrete provider type shares one IOptions<T> registration
        // regardless of the name it is registered under. PasswordResetTokenProvider<T>
        // exists specifically to resolve a distinct options type instead - this asserts
        // that distinction actually holds in the real, built container.
        var defaultLifespan = _factory.Services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value.TokenLifespan;
        var resetLifespan = _factory.Services.GetRequiredService<IOptions<PasswordResetTokenProviderOptions>>().Value.TokenLifespan;

        Assert.Equal(TimeSpan.FromHours(1), resetLifespan);
        Assert.NotEqual(defaultLifespan, resetLifespan);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();
}
