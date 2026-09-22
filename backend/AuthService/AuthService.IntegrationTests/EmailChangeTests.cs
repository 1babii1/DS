using System.Net;
using System.Net.Http.Json;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace AuthService.IntegrationTests;

// Full email-change ceremony: request (password-gated), confirm via the emailed link (which
// this test follows exactly the way a real GET-the-link click would), and the two things
// easy to get subtly wrong - UserName drifting from the new Email, and old sessions/tokens
// staying valid after the account's recovery destination changed.
public class EmailChangeTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public EmailChangeTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Requesting_a_change_with_the_wrong_password_is_rejected()
    {
        var (client, _, _) = await SignedInClientAsync();

        var response = await client.PostAsJsonAsync(
            "/auth/email/request-change", new RequestEmailChangeRequest($"new-{Guid.NewGuid():N}@test.local", "WrongPassword999"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.password_invalid", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Requesting_a_change_to_an_email_already_in_use_is_rejected()
    {
        var (client, _, password) = await SignedInClientAsync();
        var (otherEmail, _) = await CreateConfirmedAccountAsync();

        var response = await client.PostAsJsonAsync(
            "/auth/email/request-change", new RequestEmailChangeRequest(otherEmail, password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.email_in_use", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Confirming_with_an_invalid_token_does_not_change_the_email()
    {
        var (client, email, _) = await SignedInClientAsync();
        var newEmail = $"new-{Guid.NewGuid():N}@test.local";

        await using var lookupScope = _factory.Services.CreateAsyncScope();
        var userId = (await lookupScope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.SingleAsync(u => u.Email == email)).Id;

        var response = await client.GetAsync(
            $"/auth/email/confirm-change?userId={userId}&newEmail={Uri.EscapeDataString(newEmail)}&token=not-a-real-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid or expired", body, StringComparison.OrdinalIgnoreCase);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Confirming_a_requested_change_updates_email_and_username_and_signs_out_other_sessions()
    {
        var (client, oldEmail, password) = await SignedInClientAsync();
        var newEmail = $"new-{Guid.NewGuid():N}@test.local";

        var requestResponse = await client.PostAsJsonAsync(
            "/auth/email/request-change", new RequestEmailChangeRequest(newEmail, password));
        Assert.Equal(HttpStatusCode.NoContent, requestResponse.StatusCode);

        var link = _factory.EmailSender.EmailChangeConfirmations.Last(c => c.ToEmail == newEmail).Link;
        var confirmResponse = await client.GetAsync(new Uri(link).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var body = await confirmResponse.Content.ReadAsStringAsync();
        Assert.Contains("Email updated", body, StringComparison.Ordinal);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var account = await db.Users.SingleAsync(u => u.Email == newEmail);
        Assert.Equal(newEmail, account.UserName);
        Assert.True(account.EmailConfirmed);
        Assert.False(await db.Users.AnyAsync(u => u.Email == oldEmail));

        // The revoke that email change triggers signs out the browser that requested it too -
        // the same "current session dies along with the rest" behavior RevokeAllSessionsAsync
        // already has everywhere else it's used.
        var afterChange = await client.GetAsync("/auth/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, afterChange.StatusCode);

        // Signing in now only works with the new email.
        using var freshClient = _factory.CreateClient();
        var loginWithNew = await freshClient.PostAsJsonAsync("/auth/login", new LoginRequest(newEmail, password));
        Assert.Equal(HttpStatusCode.NoContent, loginWithNew.StatusCode);

        using var anotherClient = _factory.CreateClient();
        var loginWithOld = await anotherClient.PostAsJsonAsync("/auth/login", new LoginRequest(oldEmail, password));
        Assert.Equal(HttpStatusCode.Unauthorized, loginWithOld.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(string Email, string Password)> CreateConfirmedAccountAsync()
    {
        var email = $"emailchange-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        return (email, password);
    }

    private async Task<(HttpClient Client, string Email, string Password)> SignedInClientAsync()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Sign-in failed unexpectedly: {response.StatusCode}");
        }

        return (client, email, password);
    }
}
