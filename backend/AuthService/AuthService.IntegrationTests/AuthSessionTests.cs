using System.Net;
using System.Net.Http.Json;
using AuthService.Web.Contracts;

namespace AuthService.IntegrationTests;

// The "active sessions" list and per-session revoke - AuthSessionService.EstablishAsync
// re-issues the Identity.Application cookie right after each of this project's several
// sign-in paths, so this also verifies the one thing that re-issue could get subtly wrong:
// whether the *right* isPersistent value survives it, read back from the cookie the original
// PasswordSignInAsync/SignInAsync call already wrote rather than guessed at.
public class AuthSessionTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;
    private readonly Func<Task> _resetDatabase;

    public AuthSessionTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Logging_in_through_the_json_api_issues_a_persistent_cookie()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var setCookie = string.Join(" ", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_login_appears_in_the_sessions_list_flagged_as_the_current_session()
    {
        var client = await SignedInClientAsync();

        var sessions = await client.GetFromJsonAsync<List<AuthSessionSummary>>("/auth/sessions");

        Assert.Single(sessions!);
        Assert.True(sessions![0].IsCurrent);
    }

    [Fact]
    public async Task Two_logins_from_the_same_account_both_appear_and_only_the_second_is_current()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var first = _factory.CreateClient();
        await first.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        using var second = _factory.CreateClient();
        await second.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var sessionsFromSecond = await second.GetFromJsonAsync<List<AuthSessionSummary>>("/auth/sessions");
        Assert.Equal(2, sessionsFromSecond!.Count);
        Assert.Single(sessionsFromSecond, s => s.IsCurrent);
    }

    [Fact]
    public async Task Revoking_a_different_session_leaves_the_current_one_working()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var current = _factory.CreateClient();
        await current.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        using var other = _factory.CreateClient();
        await other.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var sessionsFromCurrent = await current.GetFromJsonAsync<List<AuthSessionSummary>>("/auth/sessions");
        var otherSessionId = sessionsFromCurrent!.Single(s => !s.IsCurrent).Id;

        var revokeResponse = await current.DeleteAsync($"/auth/sessions/{otherSessionId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        // The session that did the revoking is still perfectly valid.
        var stillWorks = await current.GetAsync("/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_session_signs_it_out_on_its_very_next_request()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        using var target = _factory.CreateClient();
        await target.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        using var admin = _factory.CreateClient();
        await admin.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        var targetSessionId = (await target.GetFromJsonAsync<List<AuthSessionSummary>>("/auth/sessions"))!
            .Single(s => s.IsCurrent).Id;

        var revokeResponse = await admin.DeleteAsync($"/auth/sessions/{targetSessionId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var rejectedResponse = await target.GetAsync("/auth/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedResponse.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_session_belonging_to_a_different_account_is_rejected()
    {
        var clientA = await SignedInClientAsync();
        var clientB = await SignedInClientAsync();
        var sessionIdOfA = (await clientA.GetFromJsonAsync<List<AuthSessionSummary>>("/auth/sessions"))![0].Id;

        var response = await clientB.DeleteAsync($"/auth/sessions/{sessionIdOfA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_all_sessions_signs_out_every_browser_not_just_the_one_that_asked()
    {
        // Regression guard: RevokeAllSessionsAsync predates AuthSession and originally only
        // rotated the security stamp (checked on its own ~30-minute interval elsewhere) and
        // signed out the calling request - a *different* browser's session_id-carrying
        // cookie kept passing OnValidatePrincipal's per-session check indefinitely until
        // AuthSessionService.RevokeAllAsync was wired into it.
        var (email, password) = await CreateConfirmedAccountAsync();
        using var browserA = _factory.CreateClient();
        await browserA.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        using var browserB = _factory.CreateClient();
        await browserB.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));

        var revokeResponse = await browserA.PostAsync("/auth/sessions/revoke-all", content: null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var browserBResponse = await browserB.GetAsync("/auth/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, browserBResponse.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<(string Email, string Password)> CreateConfirmedAccountAsync()
    {
        var email = $"session-{Guid.NewGuid():N}@test.local";
        const string password = "TestPass123";
        using var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new RegisterRequest(email, password));
        await client.GetAsync(_factory.EmailSender.Confirmations.Last(c => c.ToEmail == email).Link);
        return (email, password);
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var (email, password) = await CreateConfirmedAccountAsync();
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, password));
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Sign-in failed unexpectedly: {response.StatusCode}");
        }

        return client;
    }
}
