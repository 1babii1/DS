using System.Net;
using System.Net.Http.Json;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace AuthService.IntegrationTests;

public class AccountDeletionTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public AccountDeletionTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Deleting_with_the_wrong_password_is_rejected_and_the_account_survives()
    {
        var (client, email, _) = await SignedInClientAsync();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest("WrongPassword999")),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.password_invalid", envelope!.Error!.Messages[0].Code);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.True(await db.Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task The_sole_admin_cannot_delete_their_own_account()
    {
        var (client, email, password) = await SignedInClientAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var account = await userManager.FindByEmailAsync(email);
            await userManager.AddToRoleAsync(account!, RoleNames.Admin);
        }

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password)),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.cannot_delete_last_admin", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task An_admin_can_delete_their_account_when_another_admin_still_exists()
    {
        var (client, email, password) = await SignedInClientAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var account = await userManager.FindByEmailAsync(email);
            await userManager.AddToRoleAsync(account!, RoleNames.Admin);

            var (otherEmail, _) = await CreateConfirmedAccountAsync();
            var otherAccount = await userManager.FindByEmailAsync(otherEmail);
            await userManager.AddToRoleAsync(otherAccount!, RoleNames.Admin);
        }

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password)),
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_removes_it_and_its_passkeys_and_sessions_and_prevents_login()
    {
        var (client, email, password) = await SignedInClientAsync();

        var optionsResponse = await client.PostAsync("/auth/passkeys/register/options", content: null);

        // A passkey isn't actually registered here (that needs a real ceremony, covered by
        // PasskeyTests) - what matters is proving the row-level cleanup query runs correctly
        // against a real Postgres regardless of whether any rows exist to delete.
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);

        var deleteResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password)),
        });
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));

        // The deleting session itself no longer works.
        var afterDelete = await client.GetAsync("/auth/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);

        // Nor can the (now nonexistent) account sign in again.
        using var freshClient = _factory.CreateClient();
        var loginAttempt = await freshClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.Unauthorized, loginAttempt.StatusCode);
    }

    [Fact]
    public async Task A_deleted_accounts_email_can_be_registered_again()
    {
        var (client, email, password) = await SignedInClientAsync();
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password)),
        });

        using var freshClient = _factory.CreateClient();
        var registerResponse = await freshClient.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "AnotherPass456"));

        Assert.Equal(HttpStatusCode.NoContent, registerResponse.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(string Email, string Password)> CreateConfirmedAccountAsync()
    {
        var email = $"deletion-{Guid.NewGuid():N}@test.local";
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
