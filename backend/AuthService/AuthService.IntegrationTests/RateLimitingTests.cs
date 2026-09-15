using System.Net;
using System.Net.Http.Json;
using AuthService.Web.Contracts;

namespace AuthService.IntegrationTests;

/// <summary>
/// Uses RateLimitedAuthTestWebFactory, not the shared AuthTestWebFactory the other
/// tests in this project use - this is the one place the limiter is meant to
/// actually trip, with its own isolated host and its own low threshold (3, not the
/// production 5) so the test doesn't need six calls to prove the same point.
/// </summary>
public class RateLimitingTests : IClassFixture<RateLimitedAuthTestWebFactory>, IAsyncLifetime
{
    private readonly RateLimitedAuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public RateLimitingTests(RateLimitedAuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Login_is_rate_limited_after_the_configured_number_of_attempts()
    {
        using var client = _factory.CreateClient();
        var request = new LoginRequest($"ratelimit-{Guid.NewGuid():N}@test.local", "WrongPassword999");

        // All three fail authentication (401) - the limiter counts requests, not
        // failures specifically, so this is enough to prove the point without needing
        // a real account.
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/auth/login", request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var fourth = await client.PostAsJsonAsync("/auth/login", request);
        Assert.Equal((HttpStatusCode)429, fourth.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();
}