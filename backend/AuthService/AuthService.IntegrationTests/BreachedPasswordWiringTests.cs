using System.Net;
using System.Net.Http.Json;
using AuthService.Web.Contracts;
using Shared;

namespace AuthService.IntegrationTests;

// Proves BreachedPasswordValidator is actually wired into Identity's own validation pipeline
// (AddPasswordValidator<T> in AuthenticationConfiguration) rather than just existing as a
// standalone, untested class - PasswordBreachCheckerTests already covers the check's own
// logic, this covers registration actually being blocked by it end-to-end.
public class BreachedPasswordWiringTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public BreachedPasswordWiringTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Registering_with_a_password_the_checker_flags_as_breached_is_rejected()
    {
        const string password = "Breached123";
        _factory.PasswordBreachChecker.MarkAsBreached(password);
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest($"breach-{Guid.NewGuid():N}@test.local", password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>();
        Assert.Contains(envelope!.Error!.Messages, m => m.Code == "PasswordBreached");
    }

    [Fact]
    public async Task Registering_with_a_password_the_checker_does_not_flag_succeeds()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest($"clean-{Guid.NewGuid():N}@test.local", "TestPass123"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();
}
