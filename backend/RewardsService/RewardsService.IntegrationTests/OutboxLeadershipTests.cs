using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;
using Shared.Outbox;

namespace RewardsService.IntegrationTests;

// Several instances of a service run the same polling publisher against one outbox. Without a rule they all read the same rows and all publish
// them: measured at 2.2 messages on the bus per row with three instances (ADR 0054). One publishes at a time (ADR 0055); these are the rules,
// against a real Postgres, with two connections that stand for two instances.
public class OutboxLeadershipTests(RewardsTestWebFactory factory) : IClassFixture<RewardsTestWebFactory>
{
    [Fact]
    public async Task While_one_instance_is_publishing_another_is_turned_away()
    {
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<RewardsDbContext>();

        await using var leader = await OutboxLeadership.TryBeginCycleAsync(firstDb, CancellationToken.None);
        var other = await OutboxLeadership.TryBeginCycleAsync(secondDb, CancellationToken.None);

        Assert.NotNull(leader);
        Assert.Null(other);
    }

    [Fact]
    public async Task When_the_publishing_instance_finishes_or_dies_the_next_one_takes_over()
    {
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<RewardsDbContext>();

        var leader = await OutboxLeadership.TryBeginCycleAsync(firstDb, CancellationToken.None);
        Assert.NotNull(leader);
        Assert.Null(await OutboxLeadership.TryBeginCycleAsync(secondDb, CancellationToken.None));

        // A cycle that ends without committing, as when the process is killed and its connection drops, releases the claim.
        await leader.DisposeAsync();

        await using var successor = await OutboxLeadership.TryBeginCycleAsync(secondDb, CancellationToken.None);
        Assert.NotNull(successor);
    }
}
