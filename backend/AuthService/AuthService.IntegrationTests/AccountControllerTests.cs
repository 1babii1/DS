using System.Net;
using System.Net.Http.Json;
using AuthService.Web.Contracts;
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
    public async Task Registering_the_same_email_twice_fails_the_second_time()
    {
        var email = $"dup-{Guid.NewGuid():N}@test.local";
        using var client = _factory.CreateClient();

        var first = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "TestPass123"));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, "AnotherPass456"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Login_with_the_correct_password_succeeds()
    {
        var email = $"login-ok-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";

        using var registerClient = _factory.CreateClient();
        var registered = await registerClient.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(email, password));
        Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);

        // A separate client with no cookies carried over from registration - Login has
        // to authenticate on its own, not ride on Register's own sign-in.
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
        // login into the logout call, the same way a browser would.
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));

        var response = await client.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();
}