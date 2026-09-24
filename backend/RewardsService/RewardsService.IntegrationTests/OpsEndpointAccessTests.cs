using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;
using Shared.Outbox;

namespace RewardsService.IntegrationTests;

// The handlers are proven elsewhere; this proves the doors: who can reach the ops routes at
// all, and that changing state additionally demands a recent step-up.
public class OpsEndpointAccessTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private const string ListUrl = "/api/rewards/ops/outbox/parked";

    private readonly RewardsTestWebFactory _factory;

    public OpsEndpointAccessTests(RewardsTestWebFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.ResetDatabaseAsync();

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected()
    {
        var response = await _factory.CreateClient().GetAsync(ListUrl);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("editor")]
    public async Task A_non_admin_is_forbidden_even_with_the_write_role(string role)
    {
        var response = await Send(HttpMethod.Get, ListUrl, role);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_list()
    {
        var response = await Send(HttpMethod.Get, ListUrl, "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Redrive_needs_step_up_on_top_of_the_admin_role()
    {
        var id = await SeedParked();
        var url = $"{ListUrl}/{id}/redrive";

        var withoutStepUp = await Send(HttpMethod.Post, url, "admin");
        Assert.Equal(HttpStatusCode.Forbidden, withoutStepUp.StatusCode);

        var withStepUp = await Send(HttpMethod.Post, url, "admin", elevated: true);
        Assert.Equal(HttpStatusCode.NoContent, withStepUp.StatusCode);
    }

    [Fact]
    public async Task An_editor_cannot_redrive_even_when_elevated()
    {
        var id = await SeedParked();

        var response = await Send(HttpMethod.Post, $"{ListUrl}/{id}/redrive", "editor", elevated: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string role, bool elevated = false)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        if (elevated)
        {
            request.Headers.Add(TestAuthHandler.ElevatedHeader, "1");
        }

        return _factory.CreateClient().SendAsync(request);
    }

    private async Task<Guid> SeedParked()
    {
        var message = OutboxMessage.Create("Thing", "agg", "{}");
        message.RecordFailure("boom", maxAttempts: 1);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }
}
