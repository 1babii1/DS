using System.Net;
using System.Net.Http.Json;
using AuthService.Domain;
using AuthService.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shared;

namespace AuthService.IntegrationTests;

public class AccountControllerTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public AccountControllerTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Register_with_valid_data_succeeds()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest($"reg-{Guid.NewGuid():N}@test.local", "TestPass123"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_a_weak_password_fails_validation()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest($"weak-{Guid.NewGuid():N}@test.local", "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registering_an_already_used_email_looks_identical_to_a_fresh_registration()
    {
        // The enumeration property this guards: nothing in the response - status code or body -
        // may tell a caller apart from the "brand new address" case below. A regression here is
        // exactly the kind that only shows up if this specific comparison is asserted.
        var email = $"dup-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "AnotherPass456"));

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_duplicate_registration_does_not_create_a_second_account_or_reset_anything()
    {
        var email = $"dup-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);

        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "SomeoneElsesPass789"));

        // The original owner's password still works - a duplicate attempt must not have reset it,
        // deleted the account, or otherwise disturbed the real one.
        var login = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, "TestPass123"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.NotNull(await userManager.FindByEmailAsync(email));
    }

    [Fact]
    public async Task A_duplicate_registration_notifies_the_existing_owner_instead_of_a_confirmation_link()
    {
        var email = $"dup-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        var confirmationsBefore = _factory.EmailSender.Confirmations.Count(c => c.ToEmail == email);

        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "AnotherPass456"));

        Assert.Equal(confirmationsBefore, _factory.EmailSender.Confirmations.Count(c => c.ToEmail == email));
        Assert.Contains(email, _factory.EmailSender.DuplicateRegistrationNotices);
    }

    [Fact]
    public async Task Concurrent_registrations_to_the_same_email_create_exactly_one_account()
    {
        // Exercises the race-fallback path specifically: the upfront FindByEmailAsync check in
        // CompleteRegistrationAsync only ever sees ONE of these as "already exists" if they are
        // truly sequential; several callers fired at once can all pass that check before any of
        // them finishes CreateAsync, so what actually prevents a second account is the database's
        // unique index - which surfaces as an uncaught PostgresException 23505, not a graceful
        // IdentityResult, when it is what stops the race (as opposed to Identity's own pre-insert
        // check, which does return one gracefully but only catches the non-racing case). A test
        // that only registers sequentially can never observe this path at all. This is a genuine
        // race, not a scripted one: mutation-tested by disabling the DB-exception catch in
        // AccountRecoveryService, which failed this exact test 3 times out of 6 runs - real, but
        // not guaranteed on every single run the way a deterministic test would be. With the catch
        // in place it was green 5/5 (plus every run of the full class). A one-off failure of only
        // this test is worth a rerun before treating it as a regression; a repeated one is not.
        var email = $"dup-{Guid.NewGuid():N}@test.local";

        // Each client must stay alive for the whole request, not just for the synchronous part of
        // dispatching it - a `using` scoped to a lambda body disposes the client (and cancels the
        // in-flight request) the moment the un-awaited Task is returned, not when it completes.
        var clients = Enumerable.Range(0, 8).Select(_ => _factory.CreateClient()).ToList();
        try
        {
            var responses = await Task.WhenAll(clients.Select((client, i) =>
                client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, $"TestPass{i}23"))));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.NotNull(await userManager.FindByEmailAsync(email));
        Assert.Single(_factory.EmailSender.Confirmations.Where(c => c.ToEmail == email));
        Assert.Equal(7, _factory.EmailSender.DuplicateRegistrationNotices.Count(e => e == email));
    }

    [Fact]
    public async Task A_weak_password_on_a_genuinely_new_address_still_fails_validation()
    {
        // Guards the fix above from over-reaching: only a DUPLICATE address is masked into a
        // 204. A brand new address with an invalid password must still be rejected normally.
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest($"weak2-{Guid.NewGuid():N}@test.local", "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_does_not_start_a_session()
    {
        var email = $"reg-nosession-{Guid.NewGuid():N}@test.local";

        // HandleCookies defaults to true, so if Register ever started issuing a session
        // cookie again, this same client would carry it straight into Logout below.
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));

        var response = await client.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_sends_a_confirmation_email()
    {
        var email = $"reg-confirm-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));

        Assert.Contains(_factory.EmailSender.Confirmations, c => c.ToEmail == email);
    }

    [Fact]
    public async Task Login_before_confirming_the_email_is_rejected_with_a_distinct_reason()
    {
        var email = $"login-unconfirmed-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));

        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        // 401 like a wrong password, but a distinct error code - the caller already
        // proved the password is correct, so this does not create a new enumeration
        // channel the way revealing "unconfirmed" at registration time would.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Equal("auth.email_not_confirmed", envelope!.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Login_with_the_correct_password_succeeds_once_the_email_is_confirmed()
    {
        var email = $"login-ok-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";

        using var registerClient = _factory.CreateClient();
        var registered = await registerClient.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(email, password));
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);

        await ConfirmEmailAsync(registerClient, email);

        // A separate client with no cookies carried over from registration - Login has
        // to authenticate on its own, not ride on any prior request's session.
        using var loginClient = _factory.CreateClient();
        var response = await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_the_wrong_password_fails_without_revealing_which_field_was_wrong()
    {
        var email = $"login-bad-{Guid.NewGuid():N}@test.local";

        using var registerClient = _factory.CreateClient();
        await registerClient.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));

        // Confirmation is checked before the password: an unconfirmed account gets
        // NotAllowed even with a wrong password, which would otherwise mask the
        // "invalid_credentials" case this test exists to prove.
        await ConfirmEmailAsync(registerClient, email);

        using var loginClient = _factory.CreateClient();
        var response = await loginClient.PostAsJsonAsync("/auth/login", new LoginRequest(email, "WrongPassword999"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Every error response in the system - AuthService included - answers in the
        // same Envelope shape, not RFC 9110 ProblemDetails. A client that already
        // knows how to read one service's errors can read all of them.
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.NotNull(envelope);
        Assert.True(envelope!.IsError);
        Assert.Equal("auth.invalid_credentials", envelope.Error!.Messages[0].Code);
    }

    [Fact]
    public async Task Login_with_an_unknown_email_fails_the_same_way_as_a_wrong_password()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest($"nobody-{Guid.NewGuid():N}@test.local", "TestPass123"));

        // Same 401 as a wrong password, not a 404 or a different error - an attacker
        // probing emails should not be able to tell "wrong password" from "no such account".
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_without_being_signed_in_is_unauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_after_logging_in_succeeds()
    {
        var email = $"logout-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";

        // HandleCookies defaults to true - this client carries the session cookie from
        // login into the logout call, the same way a browser would. Register no longer
        // starts a session by itself, so a real Login is what actually establishes it here.
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await ConfirmEmailAsync(client, email);
        await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var response = await client.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Reads the confirmation link AuthTestWebFactory's FakeEmailSender captured
    /// for this email and follows it - the same single GET a real user's browser makes
    /// from the emailed link. Consumes the ONE most recently captured link for this
    /// address so tests that trigger multiple sends (resend, re-registration) still pick
    /// up the right one.</summary>
    private async Task ConfirmEmailAsync(HttpClient client, string email)
    {
        var link = _factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link;
        var response = await client.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();
}