using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using McpServer.Agent;
using McpServer.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static McpServer.IntegrationTests.AgentTestSupport;

namespace McpServer.IntegrationTests;

// The user's side of the bargain: confirmation is a plain authenticated request, run through the real
// pipeline with the real authorization attribute, and it accepts only a genuine plan that belongs to the caller.
public class AgentEndpointTests : IAsyncLifetime
{
    private const string Url = "/mcp/plans/confirm";

    private readonly RecordingService _employees = new((_, _) => RecordingService.Ok(Guid.NewGuid()));
    private readonly RecordingService _rewards = new((_, _) => RecordingService.Ok(Guid.NewGuid()));
    private WebApplicationFactory<Program> _factory = null!;

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
        : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("sub", sub.ToString()) };
            if (Request.Headers.ContainsKey("X-Test-Elevated"))
            {
                claims.Add(new Claim("elevated_until", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString()));
            }

            var identity = new ClaimsIdentity(claims, "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = "Test";
                o.DefaultAuthenticateScheme = "Test";
                o.DefaultChallengeScheme = "Test";
            });

            // Only the innermost handler is replaced: forwarding and resilience from Program.cs stay in the chain.
            services.AddHttpClient<EmployeeCommandClient>().ConfigurePrimaryHttpMessageHandler(() => _employees);
            services.AddHttpClient<RewardsCommandClient>().ConfigurePrimaryHttpMessageHandler(() => _rewards);
        }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private PlanSigner Signer => _factory.Services.GetRequiredService<PlanSigner>();

    private string TokenFor(Guid user, DateTimeOffset? expires = null, params PlanStep[] steps) => Signer.Sign(new Plan(
        Guid.NewGuid(), user, DateTimeOffset.UtcNow, expires ?? DateTimeOffset.UtcNow.AddMinutes(10),
        steps.Length == 0 ? [Hire()] : steps));

    private Task<HttpResponseMessage> Confirm(
        string? token, Guid? user, string authorization = "Bearer user-session-token", bool elevated = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(new { token }) };
        request.Headers.Add("Authorization", authorization);
        if (user is not null)
        {
            request.Headers.Add("X-Test-Sub", user.ToString());
        }

        if (elevated)
        {
            request.Headers.Add("X-Test-Elevated", "1");
        }

        return _factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task Confirming_without_being_signed_in_is_unauthorized_and_runs_nothing()
    {
        var user = Guid.NewGuid();

        var response = await Confirm(TokenFor(user), user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_employees.Requests);
    }

    [Fact]
    public async Task The_owner_confirming_runs_the_plan_as_themselves()
    {
        var user = Guid.NewGuid();

        var response = await Confirm(TokenFor(user), user);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_employees.Requests);
        Assert.Equal("Bearer user-session-token", sent.Authorization);
        Assert.Contains("\"completed\":true", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plan_that_hands_out_currency_needs_a_recent_reverification_and_runs_nothing_without_it()
    {
        var user = Guid.NewGuid();
        var token = TokenFor(user, steps: [Hire(), GrantToHired(50m)]);

        var response = await Confirm(token, user, elevated: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_employees.Requests);
        Assert.Empty(_rewards.Requests);
    }

    [Fact]
    public async Task The_same_plan_runs_once_the_caller_has_reverified()
    {
        var user = Guid.NewGuid();
        var token = TokenFor(user, steps: [Hire(), GrantToHired(50m)]);

        var response = await Confirm(token, user, elevated: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_employees.Requests);
        Assert.Single(_rewards.Requests);
    }

    [Fact]
    public async Task A_plan_without_any_currency_does_not_ask_for_reverification()
    {
        var user = Guid.NewGuid();

        var response = await Confirm(TokenFor(user), user, elevated: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Someone_elses_plan_is_forbidden_and_runs_nothing()
    {
        var owner = Guid.NewGuid();

        var response = await Confirm(TokenFor(owner), user: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_employees.Requests);
    }

    [Fact]
    public async Task An_expired_plan_is_gone_and_runs_nothing()
    {
        var user = Guid.NewGuid();

        var response = await Confirm(TokenFor(user, expires: DateTimeOffset.UtcNow.AddSeconds(-1)), user);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Empty(_employees.Requests);
    }

    [Fact]
    public async Task A_plan_altered_after_signing_is_rejected_and_runs_nothing()
    {
        var user = Guid.NewGuid();
        var parts = TokenFor(user, steps: [GrantToHiredOnly()]).Split('.');
        var flipped = parts[2][..^2] + (parts[2][^2] == 'A' ? "B" : "A") + parts[2][^1];

        var response = await Confirm($"{parts[0]}.{parts[1]}.{flipped}", user);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_employees.Requests);
        Assert.Empty(_rewards.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public async Task A_missing_or_garbage_token_is_a_bad_request_and_runs_nothing(string? token)
    {
        var response = await Confirm(token, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_employees.Requests);
    }

    [Fact]
    public async Task The_failure_answers_reveal_nothing_about_why_a_signature_failed()
    {
        var response = await Confirm("v1.abc.def", Guid.NewGuid());

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"error":"This is not a valid plan."}""", body);
    }

    private static PlanStep GrantToHiredOnly() => new(StepKind.GrantCurrency, "g", EmployeeId: Guid.NewGuid(), Amount: 5m, Reason: "r");
}
