using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Outbox;

namespace AuthService.IntegrationTests;

// Covers the auth.events security audit trail - login success/failure/lockout and
// password-change each land in the outbox as their own row, driven end-to-end over the
// JSON API (the Razor Pages call the exact same SecurityAuditService, so this does not
// re-verify the same assertion through both entry points).
public class SecurityAuditTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public SecurityAuditTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task A_successful_login_records_a_LoginSucceeded_event()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var count = await OutboxCountAsync("LoginSucceeded", email);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task A_wrong_password_records_a_LoginFailed_event_with_the_invalid_credentials_reason()
    {
        var (email, _) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, "TotallyWrongPassword1"));

        var payload = await OutboxPayloadAsync("LoginFailed", email);
        Assert.Contains("\"invalid_credentials\"", payload);
    }

    [Fact]
    public async Task An_unconfirmed_login_attempt_records_a_LoginFailed_event_with_the_confirmation_reason()
    {
        var email = $"audit-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));

        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var payload = await OutboxPayloadAsync("LoginFailed", email);
        Assert.Contains("\"email_not_confirmed\"", payload);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_and_record_an_AccountLockedOut_event()
    {
        var (email, _) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();

        // AuthenticationConfiguration sets MaxFailedAccessAttempts to 5 - the 5th failure
        // itself trips the lockout (PasswordSignInAsync locks out immediately once the
        // access-failed count reaches the threshold), so this only needs 5 attempts.
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, "StillWrongPassword1"));
        }

        var count = await OutboxCountAsync("AccountLockedOut", email);
        Assert.True(count >= 1, "expected at least one AccountLockedOut event after repeated failures");
    }

    [Fact]
    public async Task A_successful_password_reset_records_a_PasswordChanged_event()
    {
        var (email, _) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/forgot-password", new ForgotPasswordRequest(email));
        var link = _factory.EmailSender.Resets.Last(r => r.ToEmail == email).Link;
        var uri = new Uri(link);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var token = query["token"]!;

        var response = await client.PostAsJsonAsync(
            "/auth/reset-password", new ResetPasswordRequest(email, token, "BrandNewPassword1"));

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        var count = await OutboxCountAsync("PasswordChanged", email);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task The_first_login_ever_does_not_send_a_new_sign_in_notification()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        Assert.DoesNotContain(_factory.EmailSender.NewSignInNotifications, n => n.ToEmail == email);
    }

    [Fact]
    public async Task Logging_in_again_from_the_same_ip_does_not_send_a_new_sign_in_notification()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        await client.PostAsync("/auth/logout", content: null);

        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        Assert.DoesNotContain(_factory.EmailSender.NewSignInNotifications, n => n.ToEmail == email);
    }

    [Fact]
    public async Task Logging_in_from_a_different_ip_than_last_time_sends_a_new_sign_in_notification()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var first = _factory.CreateClient();
        first.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        await first.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        using var second = _factory.CreateClient();
        second.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.20");
        await second.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var notification = _factory.EmailSender.NewSignInNotifications.Last(n => n.ToEmail == email);
        Assert.Equal("198.51.100.20", notification.IpAddress);
    }

    [Fact]
    public async Task Revoking_all_sessions_signs_out_the_current_cookie_and_rejects_an_existing_refresh_token()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var (verifier, challenge) = GeneratePkcePair();
        const string redirectUri = "http://localhost:3000/auth/callback";
        var authorizeUrl = "/connect/authorize" +
            $"?client_id=portfolio-frontend&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=" + Uri.EscapeDataString("openid offline_access") +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=x";
        var authorizeResponse = await client.GetAsync(authorizeUrl);
        var code = System.Web.HttpUtility.ParseQueryString(authorizeResponse.Headers.Location!.Query)["code"]!;
        var tokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = "portfolio-frontend",
            ["code_verifier"] = verifier,
        }));
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        var revoke = await client.PostAsync("/auth/sessions/revoke-all", content: null);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, revoke.StatusCode);

        // Cookie session: the very next authenticated call must be rejected.
        var afterRevoke = await client.PostAsync("/auth/sessions/revoke-all", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, afterRevoke.StatusCode);

        // OAuth session: the refresh token minted before the revoke must no longer work.
        var refreshAttempt = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "portfolio-frontend",
        }));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refreshAttempt.StatusCode);

        var eventCount = await OutboxCountAsync("AllSessionsRevoked", email);
        Assert.Equal(1, eventCount);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(string Email, string Password)> CreateConfirmedAccountAsync()
    {
        var email = $"audit-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        return (email, password);
    }

    // Payload is stored as jsonb, which Postgres will not compare with a plain LIKE/
    // Contains - the Type filter narrows the row count enough that filtering the rest
    // in memory is simpler than teaching EF a jsonb-aware predicate for a test helper.
    private async Task<int> OutboxCountAsync(string type, string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var messages = await db.Set<OutboxMessage>().Where(m => m.Type == type).ToListAsync();
        return messages.Count(m => m.Payload.Contains(email, StringComparison.Ordinal));
    }

    private async Task<string> OutboxPayloadAsync(string type, string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var messages = await db.Set<OutboxMessage>().Where(m => m.Type == type).ToListAsync();
        return messages
            .Where(m => m.Payload.Contains(email, StringComparison.Ordinal))
            .OrderByDescending(m => m.OccurredAt)
            .First()
            .Payload;
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
