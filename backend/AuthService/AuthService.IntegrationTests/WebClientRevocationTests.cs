using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.IntegrationTests;

// The Next.js BFF signs a person out by clearing the tokens it stores, which stops it using them but leaves the
// refresh token valid at the issuer. The confidential "portfolio-web" client can now ask the issuer to revoke it
// (server to server, with its own credentials), so a refresh token that was copied out of the BFF's database is dead
// from the moment of sign-out. Access tokens are short-lived and stay valid until they expire (see ADR 0021).
public sealed class WebClientAuthTestWebFactory : AuthTestWebFactory
{
    // A test-only value, long enough to satisfy the client's own length rule; not a credential for anything.
    public const string ClientSecret = "test-only-web-client-secret-0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:WebClient:Enabled", "true");
        builder.UseSetting("Auth:WebClient:ClientSecret", ClientSecret);
        base.ConfigureWebHost(builder);
    }
}

public class WebClientRevocationTests : IClassFixture<WebClientAuthTestWebFactory>, IAsyncLifetime
{
    private const string RedirectUri = "http://localhost:3000/api/auth/callback/openiddict";
    private const string ClientId = "portfolio-web";

    private readonly WebClientAuthTestWebFactory _factory;

    public WebClientRevocationTests(WebClientAuthTestWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.ResetDatabaseAsync();

    [Fact]
    public async Task A_revoked_refresh_token_can_no_longer_be_exchanged()
    {
        var refreshToken = await SignInAsync();
        using var client = _factory.CreateClient();

        var revoked = await client.PostAsync("/connect/revocation", Form(new()
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = ClientId,
            ["client_secret"] = WebClientAuthTestWebFactory.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        var refreshed = await client.PostAsync("/connect/token", Form(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_secret"] = WebClientAuthTestWebFactory.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refreshed.StatusCode);
        Assert.Equal("invalid_grant", (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Revoking_again_is_safe()
    {
        var refreshToken = await SignInAsync();
        using var client = _factory.CreateClient();
        var request = new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = ClientId,
            ["client_secret"] = WebClientAuthTestWebFactory.ClientSecret,
        };

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/connect/revocation", Form(request))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/connect/revocation", Form(request))).StatusCode);
    }

    [Fact]
    public async Task Without_the_clients_secret_nothing_is_revoked()
    {
        var refreshToken = await SignInAsync();
        using var client = _factory.CreateClient();

        var attempt = await client.PostAsync("/connect/revocation", Form(new()
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = ClientId,
            ["client_secret"] = "not-the-secret-not-the-secret-not-the-secret",
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);

        var refreshed = await client.PostAsync("/connect/token", Form(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["client_secret"] = WebClientAuthTestWebFactory.ClientSecret,
        }));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Fact]
    public async Task The_browser_facing_public_client_may_not_use_the_revocation_endpoint()
    {
        var refreshToken = await SignInAsync();
        using var client = _factory.CreateClient();

        var attempt = await client.PostAsync("/connect/revocation", Form(new()
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = "portfolio-frontend",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> values) => new(values);

    // A confirmed account, signed in, taken through the authorization-code flow as the confidential web client.
    private async Task<string> SignInAsync()
    {
        var email = $"revoke-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = await client.GetAsync(
            $"/connect/authorize?client_id={ClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid offline_access")}&code_challenge={challenge}&code_challenge_method=S256&state=x");
        var code = System.Web.HttpUtility.ParseQueryString(authorize.Headers.Location!.Query)["code"]!;

        var token = await client.PostAsync("/connect/token", Form(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["client_secret"] = WebClientAuthTestWebFactory.ClientSecret,
            ["code_verifier"] = verifier,
        }));
        token.EnsureSuccessStatusCode();
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString()!;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
