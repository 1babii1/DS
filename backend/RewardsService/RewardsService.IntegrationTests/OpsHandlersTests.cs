using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;
using Shared.Ops;
using Shared.Outbox;

namespace RewardsService.IntegrationTests;

// The handlers are shared by every service; Rewards is where they are proven against a real
// Postgres, since they only need a DbContext that maps OutboxMessage.
public class OpsHandlersTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public OpsHandlersTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task Only_parked_messages_are_listed_and_the_payload_stays_out_of_the_list()
    {
        var parked = await Seed(park: true);
        await Seed(park: false);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var list = await OpsHandlers.ListParkedAsync(db, 50, CancellationToken.None);

        var only = Assert.Single(list);
        Assert.Equal(parked, only.Id);
        Assert.Equal("boom", only.LastError);
    }

    [Fact]
    public async Task Detail_returns_the_payload_for_a_parked_message_and_nothing_for_an_unparked_one()
    {
        var parked = await Seed(park: true);
        var healthy = await Seed(park: false);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

        var detail = await OpsHandlers.GetParkedAsync(db, parked, CancellationToken.None);
        Assert.Contains("\"a\"", detail!.Payload);
        Assert.Null(await OpsHandlers.GetParkedAsync(db, healthy, CancellationToken.None));
    }

    [Fact]
    public async Task Redrive_unparks_the_message_and_records_who_did_it_in_the_same_commit()
    {
        var id = await Seed(park: true);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var outcome = await OpsHandlers.RedriveAsync(db, id, "admin-42", CancellationToken.None);

        Assert.Equal(RedriveOutcome.Redriven, outcome);

        await using var verify = _services.CreateAsyncScope();
        var check = verify.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var message = await check.Set<OutboxMessage>().SingleAsync(m => m.Id == id);
        Assert.Null(message.ParkedAt);
        Assert.Equal(0, message.AttemptCount);
        Assert.Null(message.ProcessedAt);

        var audit = await check.Set<OutboxMessage>().SingleAsync(m => m.Type == OpsHandlers.RedrivenEventType);
        Assert.Contains("admin-42", audit.Payload);
        Assert.Contains(id.ToString(), audit.Payload);
    }

    [Fact]
    public async Task Redriving_a_message_that_is_not_parked_changes_nothing()
    {
        var healthy = await Seed(park: false);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

        Assert.Equal(RedriveOutcome.NotParked, await OpsHandlers.RedriveAsync(db, healthy, "a", CancellationToken.None));
        Assert.Equal(RedriveOutcome.NotFound, await OpsHandlers.RedriveAsync(db, Guid.NewGuid(), "a", CancellationToken.None));
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync());
    }

    private async Task<Guid> Seed(bool park)
    {
        var message = OutboxMessage.Create("Thing", Guid.NewGuid().ToString(), "{\"a\":1}");
        if (park)
        {
            message.RecordFailure("boom", maxAttempts: 1);
        }

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        db.Set<OutboxMessage>().Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }
}
