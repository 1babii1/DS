using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace AuthService.IntegrationTests;

// AdminController is bearer/OIDC-authenticated (not the Identity.Application cookie the
// login-UI endpoints use), because [RequireStepUp] only ever finds elevated_until on an
// access token minted by AuthorizationController.Exchange - so every mutating test here
// drives a real PKCE flow to get a token, then a real step-up-and-refresh to elevate it,
// the same two-stage shape StepUpTests already proved works end-to-end.
public class AdminAccountTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private const string RedirectUri = "http://localhost:3000/auth/callback";

    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public AdminAccountTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task A_non_admin_account_is_forbidden_from_the_admin_surface()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        var (_, accessToken, _) = await SignInAndAuthorizeAsync(email, password);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.GetAsync("/admin/accounts");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_list_and_search_accounts()
    {
        var (targetEmail, _) = await CreateConfirmedAccountAsync();
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        await PromoteToAdminAsync(adminEmail);
        var (_, accessToken, _) = await SignInAndAuthorizeAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var searchTerm = targetEmail[..targetEmail.IndexOf('@', StringComparison.Ordinal)];
        var response = await client.GetFromJsonAsync<JsonElement>($"/admin/accounts?search={searchTerm}");

        var items = response.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(targetEmail, items[0].GetProperty("email").GetString());
    }

    [Fact]
    public async Task Setting_roles_without_a_prior_step_up_is_forbidden()
    {
        var (targetEmail, _) = await CreateConfirmedAccountAsync();
        var targetId = await GetAccountIdAsync(targetEmail);
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        await PromoteToAdminAsync(adminEmail);
        var (_, accessToken, _) = await SignInAndAuthorizeAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.PutAsJsonAsync(
            $"/admin/accounts/{targetId}/roles", new SetRolesRequest([RoleNames.Editor]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_change_another_accounts_roles_after_stepping_up()
    {
        var (targetEmail, _) = await CreateConfirmedAccountAsync();
        var targetId = await GetAccountIdAsync(targetEmail);
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        await PromoteToAdminAsync(adminEmail);
        var elevatedToken = await SignInAndStepUpAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", elevatedToken);
        var response = await client.PutAsJsonAsync(
            $"/admin/accounts/{targetId}/roles", new SetRolesRequest([RoleNames.Editor]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var updated = await client.GetFromJsonAsync<AdminAccountSummary>($"/admin/accounts/{targetId}");
        Assert.Equal([RoleNames.Editor], updated!.Roles);
    }

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_role()
    {
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        var adminId = await PromoteToAdminAsync(adminEmail);
        var elevatedToken = await SignInAndStepUpAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", elevatedToken);
        var response = await client.PutAsJsonAsync(
            $"/admin/accounts/{adminId}/roles", new SetRolesRequest([RoleNames.Viewer]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("admin.cannot_remove_own_admin_role", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Setting_an_unknown_role_name_is_rejected()
    {
        var (targetEmail, _) = await CreateConfirmedAccountAsync();
        var targetId = await GetAccountIdAsync(targetEmail);
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        await PromoteToAdminAsync(adminEmail);
        var elevatedToken = await SignInAndStepUpAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", elevatedToken);
        var response = await client.PutAsJsonAsync(
            $"/admin/accounts/{targetId}/roles", new SetRolesRequest(["superuser"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("admin.invalid_roles", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Locking_an_account_revokes_its_existing_refresh_token()
    {
        var (targetEmail, targetPassword) = await CreateConfirmedAccountAsync();
        var (targetId, _, targetRefreshToken) = await SignInAndAuthorizeAsync(targetEmail, targetPassword);
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        await PromoteToAdminAsync(adminEmail);
        var elevatedToken = await SignInAndStepUpAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", elevatedToken);
        var lockResponse = await client.PostAsJsonAsync($"/admin/accounts/{targetId}/lock", new LockAccountRequest(null));
        Assert.Equal(HttpStatusCode.NoContent, lockResponse.StatusCode);

        using var anonymous = _factory.CreateClient();
        var refreshAttempt = await anonymous.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = targetRefreshToken,
            ["client_id"] = "portfolio-frontend",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refreshAttempt.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_lock_their_own_account()
    {
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        var adminId = await PromoteToAdminAsync(adminEmail);
        var elevatedToken = await SignInAndStepUpAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", elevatedToken);
        var response = await client.PostAsJsonAsync($"/admin/accounts/{adminId}/lock", new LockAccountRequest(null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("admin.cannot_lock_own_account", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Unlocking_clears_the_lockout()
    {
        var (targetEmail, _) = await CreateConfirmedAccountAsync();
        var targetId = await GetAccountIdAsync(targetEmail);
        var (adminEmail, adminPassword) = await CreateConfirmedAccountAsync();
        await PromoteToAdminAsync(adminEmail);
        var elevatedToken = await SignInAndStepUpAsync(adminEmail, adminPassword);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", elevatedToken);
        await client.PostAsJsonAsync($"/admin/accounts/{targetId}/lock", new LockAccountRequest(null));

        var unlockResponse = await client.PostAsync($"/admin/accounts/{targetId}/unlock", content: null);
        Assert.Equal(HttpStatusCode.NoContent, unlockResponse.StatusCode);

        var afterUnlock = await client.GetFromJsonAsync<AdminAccountSummary>($"/admin/accounts/{targetId}");
        Assert.Null(afterUnlock!.LockoutEnd);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(string Email, string Password)> CreateConfirmedAccountAsync()
    {
        var email = $"admin-test-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        return (email, password);
    }

    private async Task<Guid> PromoteToAdminAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var account = await userManager.FindByEmailAsync(email) ?? throw new InvalidOperationException("Account not found.");
        await userManager.AddToRoleAsync(account, RoleNames.Admin);
        return account.Id;
    }

    private async Task<Guid> GetAccountIdAsync(string email)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return (await db.Users.SingleAsync(a => a.Email == email)).Id;
    }

    /// <summary>Logs in, drives a real PKCE authorization_code exchange, and returns the
    /// account id plus the resulting access/refresh tokens - the un-elevated baseline every
    /// step-up-forbidden test starts from.</summary>
    private async Task<(Guid AccountId, string AccessToken, string RefreshToken)> SignInAndAuthorizeAsync(
        string email, string password)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
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

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var account = await userManager.FindByEmailAsync(email) ?? throw new InvalidOperationException("Account not found.");

        return (account.Id, tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("refresh_token").GetString()!);
    }

    /// <summary>The full elevation path: sign in, request an authorization code, step up via
    /// the emailed code (this account has no 2FA), then exchange for a *refreshed* access
    /// token - only that refreshed token carries elevated_until, exactly as StepUpTests
    /// proved for the login/refresh cycle itself.</summary>
    private async Task<string> SignInAndStepUpAsync(string email, string password)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
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
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        await client.PostAsync("/auth/step-up/request-email-code", content: null);
        var emailedCode = _factory.EmailSender.StepUpCodes.Last(c => c.ToEmail == email).Code;
        var verifyResponse = await client.PostAsJsonAsync("/auth/step-up/verify", new StepUpVerifyRequest(emailedCode));
        if (verifyResponse.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Step-up verification failed unexpectedly: {verifyResponse.StatusCode}");
        }

        var refreshedTokenResponse = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "portfolio-frontend",
        }));
        var refreshedTokens = await refreshedTokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return refreshedTokens.GetProperty("access_token").GetString()!;
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
