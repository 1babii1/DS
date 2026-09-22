using System.Net;
using System.Net.Http.Json;
using AuthService.Application;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.Web.Contracts;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared;

namespace AuthService.IntegrationTests;

// Drives the real Fido2NetLib registration/authentication ceremonies end-to-end via
// Fido2TestAuthenticator (a hand-built "none"-attestation authenticator - real ECDSA P-256
// keys, real CBOR encoding), not just PasskeyService's own database plumbing. If the RPID/
// origin config, the CBOR shape, or the signature format were wrong, these would fail exactly
// the way a real browser's rejected ceremony would - a mocked authenticator could not tell
// the difference.
public class PasskeyTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;
    private string _origin = string.Empty;
    private string _rpId = string.Empty;

    public PasskeyTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Listing_passkeys_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/auth/passkeys");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_newly_registered_account_has_no_passkeys()
    {
        var client = await SignedInClientAsync();

        var passkeys = await client.GetFromJsonAsync<List<PasskeySummary>>("/auth/passkeys");

        Assert.Empty(passkeys!);
    }

    [Fact]
    public async Task Registering_a_passkey_with_the_real_ceremony_persists_it()
    {
        var client = await SignedInClientAsync();
        var authenticator = new Fido2TestAuthenticator();

        await RegisterPasskeyAsync(client, authenticator, "My test key");

        var passkeys = await client.GetFromJsonAsync<List<PasskeySummary>>("/auth/passkeys");
        Assert.Single(passkeys!);
        Assert.Equal("My test key", passkeys![0].Name);
    }

    [Fact]
    public async Task Registering_with_a_stale_challenge_token_is_rejected()
    {
        var client = await SignedInClientAsync();
        var authenticator = new Fido2TestAuthenticator();

        var optionsResponse = await client.PostAsync("/auth/passkeys/register/options", content: null);
        var options = await optionsResponse.Content.ReadFromJsonAsync<PasskeyChallengeResponse>();
        var parsed = System.Text.Json.JsonDocument.Parse(options!.OptionsJson).RootElement;
        var challenge = parsed.GetProperty("challenge").GetString()!;
        var attestation = authenticator.MakeAttestation(challenge, _origin, _rpId);

        var completeResponse = await client.PostAsJsonAsync(
            "/auth/passkeys/register/complete",
            new CompletePasskeyRegistrationRequest("not-a-real-token", "Key", attestation));

        Assert.Equal(HttpStatusCode.BadRequest, completeResponse.StatusCode);
    }

    [Fact]
    public async Task A_registered_passkey_can_be_deleted()
    {
        var client = await SignedInClientAsync();
        var authenticator = new Fido2TestAuthenticator();
        await RegisterPasskeyAsync(client, authenticator, "Delete me");
        var passkeys = await client.GetFromJsonAsync<List<PasskeySummary>>("/auth/passkeys");

        var deleteResponse = await client.DeleteAsync($"/auth/passkeys/{passkeys![0].Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        var afterDelete = await client.GetFromJsonAsync<List<PasskeySummary>>("/auth/passkeys");
        Assert.Empty(afterDelete!);
    }

    [Fact]
    public async Task Deleting_a_nonexistent_passkey_returns_not_found()
    {
        var client = await SignedInClientAsync();

        var response = await client.DeleteAsync($"/auth/passkeys/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_with_a_registered_passkey_the_usernameless_way_succeeds()
    {
        var client = await SignedInClientAsync();
        var authenticator = new Fido2TestAuthenticator();
        await RegisterPasskeyAsync(client, authenticator, "Login key");
        await client.PostAsync("/auth/logout", content: null);

        using var anonymousClient = _factory.CreateClient();
        var optionsResponse = await anonymousClient.PostAsync("/auth/passkeys/login/options", content: null);
        var options = await optionsResponse.Content.ReadFromJsonAsync<PasskeyChallengeResponse>();
        var parsed = System.Text.Json.JsonDocument.Parse(options!.OptionsJson).RootElement;
        var challenge = parsed.GetProperty("challenge").GetString()!;

        var assertion = authenticator.MakeAssertion(challenge, _origin, _rpId, userHandle: null);
        var completeResponse = await anonymousClient.PostAsJsonAsync(
            "/auth/passkeys/login/complete", new CompletePasskeyLoginRequest(options.Token, assertion));

        Assert.Equal(HttpStatusCode.NoContent, completeResponse.StatusCode);

        // Proves a real Identity.Application session was granted, not just a 204: only an
        // authenticated caller can reach logout successfully.
        var logoutResponse = await anonymousClient.PostAsync("/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
    }

    [Fact]
    public async Task Signing_in_with_an_unregistered_passkey_fails()
    {
        using var client = _factory.CreateClient();
        var strangerAuthenticator = new Fido2TestAuthenticator();

        var optionsResponse = await client.PostAsync("/auth/passkeys/login/options", content: null);
        var options = await optionsResponse.Content.ReadFromJsonAsync<PasskeyChallengeResponse>();
        var parsed = System.Text.Json.JsonDocument.Parse(options!.OptionsJson).RootElement;
        var challenge = parsed.GetProperty("challenge").GetString()!;

        var assertion = strangerAuthenticator.MakeAssertion(challenge, _origin, _rpId, userHandle: null);
        var completeResponse = await client.PostAsJsonAsync(
            "/auth/passkeys/login/complete", new CompletePasskeyLoginRequest(options!.Token, assertion));

        Assert.Equal(HttpStatusCode.Unauthorized, completeResponse.StatusCode);
    }

    public Task InitializeAsync()
    {
        var issuer = new Uri(_factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value.Issuer);
        _origin = issuer.GetLeftPart(UriPartial.Authority);
        _rpId = issuer.Host;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<HttpClient> SignedInClientAsync()
    {
        var email = $"passkey-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Sign-in failed unexpectedly: {response.StatusCode}");
        }

        return client;
    }

    private async Task RegisterPasskeyAsync(HttpClient client, Fido2TestAuthenticator authenticator, string label)
    {
        var optionsResponse = await client.PostAsync("/auth/passkeys/register/options", content: null);
        var options = await optionsResponse.Content.ReadFromJsonAsync<PasskeyChallengeResponse>();
        var parsed = System.Text.Json.JsonDocument.Parse(options!.OptionsJson).RootElement;
        var challenge = parsed.GetProperty("challenge").GetString()!;

        var attestation = authenticator.MakeAttestation(challenge, _origin, _rpId);
        var completeResponse = await client.PostAsJsonAsync(
            "/auth/passkeys/register/complete",
            new CompletePasskeyRegistrationRequest(options.Token, label, attestation));

        if (completeResponse.StatusCode != HttpStatusCode.NoContent)
        {
            var body = await completeResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Passkey registration failed unexpectedly: {completeResponse.StatusCode} {body}");
        }
    }
}
