using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;
using Shared.Kafka;
using Shared.Ops;
using Shared.Outbox;

namespace RewardsService.IntegrationTests;

// The numbers the alerts are built on. A wrong count here means an alert that stays silent
// during exactly the failure it exists for, so each figure is pinned against a real database.
public class OpsSnapshotTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public OpsSnapshotTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task An_empty_database_reports_all_zeros()
    {
        var snapshot = await Read();

        Assert.Equal(new OpsSnapshot(0, 0, 0, 0), snapshot);
    }

    [Fact]
    public async Task Parked_pending_and_dead_letters_are_counted_separately()
    {
        var processed = OutboxMessage.Create("A", "1", "{}");
        processed.MarkProcessed();
        var pending = OutboxMessage.Create("B", "2", "{}");
        var parked = OutboxMessage.Create("C", "3", "{}");
        parked.RecordFailure("boom", maxAttempts: 1);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            db.Set<OutboxMessage>().AddRange(processed, pending, parked);
            db.DeadLetters.Add(DeadLetterEntry.Create(Guid.NewGuid(), "t", "k", "{}", "err", 3));
            await db.SaveChangesAsync();
        }

        var snapshot = await Read();

        Assert.Equal(1, snapshot.Parked);
        Assert.Equal(1, snapshot.Pending);
        Assert.Equal(1, snapshot.DeadLetters);
    }

    [Fact]
    public async Task The_oldest_pending_age_ignores_parked_and_processed_messages()
    {
        // The parked message is created first so it is genuinely the older one: if it leaked into
        // the "oldest pending" figure, the age would come out over a second too high.
        var parked = OutboxMessage.Create("C", "3", "{}");
        parked.RecordFailure("boom", maxAttempts: 1);
        await Task.Delay(1200);
        var pending = OutboxMessage.Create("B", "2", "{}");

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            db.Set<OutboxMessage>().AddRange(pending, parked);
            await db.SaveChangesAsync();
        }

        await using var read = _services.CreateAsyncScope();
        var context = read.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var snapshot = await OpsSnapshotReader.ReadAsync(context, pending.OccurredAt.AddSeconds(90), CancellationToken.None);

        // Measured against the pending message, not the parked one that is also unpublished.
        Assert.InRange(snapshot.OldestPendingAgeSeconds, 89.5, 90.5);
    }

    private async Task<OpsSnapshot> Read()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        return await OpsSnapshotReader.ReadAsync(db, DateTime.UtcNow, CancellationToken.None);
    }
}
