using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using RewardsService.Infrastructure.Consumers;
using Shared.Outbox;

namespace ChaosTests;

// The real outbox publisher, a real Kafka, the real consumer and a real Postgres, with one of
// them frozen mid-flight. The claim under test is the one the platform's design rests on: a
// hire's welcome bonus is never lost and never granted twice, whatever hangs.
//
// The "employee" outbox rows live in the Rewards database here, standing in for EmployeeService's
// own outbox: what is being exercised is the publish -> consume -> grant path, which is identical.
public class WelcomeBonusUnderFaultTests : IClassFixture<ChaosStack>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    private readonly ChaosStack _stack;

    public WelcomeBonusUnderFaultTests(ChaosStack stack) => _stack = stack;

    [Fact]
    public async Task A_frozen_broker_delays_bonuses_but_loses_and_duplicates_none()
    {
        var topic = $"employee.events.{Guid.NewGuid():N}";
        await using var pipeline = await Pipeline.StartAsync(_stack, topic);

        // Baseline: the path works before anything is broken.
        var warmup = await Hire(10);
        await Eventually(() => AccountedFor(warmup), expected: warmup.Count);

        // Freeze the broker, then hire while it is hung.
        await _stack.FreezeKafkaAsync();
        IReadOnlyList<Guid> hiredWhileFrozen;
        try
        {
            hiredWhileFrozen = await Hire(20);
            await Task.Delay(TimeSpan.FromSeconds(6));

            // Nothing can have been delivered through a frozen broker.
            Assert.Equal(0, await AccountedFor(hiredWhileFrozen));
        }
        finally
        {
            await _stack.ThawKafkaAsync();
        }

        await Eventually(() => AccountedFor(hiredWhileFrozen), expected: hiredWhileFrozen.Count);

        await AssertNeverDoubledOrLost([.. warmup, .. hiredWhileFrozen], allowDeadLetters: false);
    }

    [Fact]
    public async Task A_frozen_database_never_loses_or_duplicates_a_bonus()
    {
        var topic = $"employee.events.{Guid.NewGuid():N}";
        await using var pipeline = await Pipeline.StartAsync(_stack, topic, startConsumer: false);

        // Get every message into Kafka first, with no consumer running. Only then is there
        // something for the consumer to hit a hung database with: started earlier, it would
        // finish all of it in a fraction of a second, before there was anything to break.
        var hired = await Hire(20);
        await Eventually(async () => await UnpublishedOutbox() == 0 ? 1 : 0, expected: 1);

        await _stack.FreezePostgresAsync();
        try
        {
            await pipeline.StartConsumerAsync();

            // Long enough that all three processing attempts AND the dead-letter write fail while
            // the database is still hung: the branch where a message can be neither handled nor
            // parked, and the consumer must refuse to move past it. Measured, not assumed - each
            // attempt against a dead database takes well over the 3s command timeout (a fresh
            // connection times out first), and at 20s the retries were still running when the
            // database came back, so the test passed even with that safeguard removed.
            await Task.Delay(TimeSpan.FromSeconds(45));
        }
        finally
        {
            await _stack.ThawPostgresAsync();
        }

        // A message whose retries were exhausted may have been dead-lettered instead of granted
        // if the database came back at just the wrong moment. That is the designed outcome (parked
        // for an operator, visible, redrivable) - what must never happen is a message that is
        // neither granted nor parked, or granted twice.
        await Eventually(() => AccountedFor(hired), expected: hired.Count);

        await AssertNeverDoubledOrLost(hired, allowDeadLetters: true);
    }

    private async Task<int> UnpublishedOutbox()
    {
        await using var scope = _stack.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        return await db.Set<OutboxMessage>().CountAsync(m => m.ProcessedAt == null);
    }

    private async Task<IReadOnlyList<Guid>> Hire(int count)
    {
        var employees = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

        await using var scope = _stack.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        db.Set<OutboxMessage>().AddRange(employees.Select(id => OutboxMessage.Create(
            EmployeeHiredEvent.MessageType, id.ToString(), JsonSerializer.Serialize(new { EmployeeId = id }))));
        await db.SaveChangesAsync();
        return employees;
    }

    // Employees whose hire has reached a final state: granted their bonus, or dead-lettered.
    private async Task<int> AccountedFor(IReadOnlyList<Guid> employees)
    {
        await using var scope = _stack.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        try
        {
            var granted = await db.Transactions.CountAsync(
                t => employees.Contains(t.EmployeeId) && t.Source == TransactionSource.WelcomeBonus);
            var deadLettered = await CountDeadLettered(db, employees);
            return granted + deadLettered;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return -1; // database unreachable right now; the caller keeps polling
        }
    }

    private static async Task<int> CountDeadLettered(RewardsDbContext db, IReadOnlyList<Guid> employees)
    {
        var keys = employees.Select(e => e.ToString()).ToList();
        return await db.DeadLetters.CountAsync(d => keys.Contains(d.MessageKey));
    }

    private async Task AssertNeverDoubledOrLost(IReadOnlyList<Guid> employees, bool allowDeadLetters)
    {
        await using var scope = _stack.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

        var bonuses = await db.Transactions
            .Where(t => employees.Contains(t.EmployeeId) && t.Source == TransactionSource.WelcomeBonus)
            .GroupBy(t => t.EmployeeId)
            .Select(g => new { Employee = g.Key, Count = g.Count() })
            .ToListAsync();
        // Only this scenario's employees: dead letters left by another scenario are not this one's.
        var deadLettered = (await db.DeadLetters.Select(d => d.MessageKey).ToListAsync())
            .Select(Guid.Parse).Where(employees.Contains).ToHashSet();

        Assert.All(bonuses, b => Assert.Equal(1, b.Count));

        var granted = bonuses.Select(b => b.Employee).ToHashSet();
        var lost = employees.Where(e => !granted.Contains(e) && !deadLettered.Contains(e)).ToList();
        Assert.Empty(lost);

        var doubleTreated = granted.Intersect(deadLettered).ToList();
        Assert.Empty(doubleTreated);

        if (!allowDeadLetters)
        {
            Assert.Empty(deadLettered);
        }

        // The wallet balance is the money: exactly one bonus each, not one per delivery.
        var balances = await db.Wallets.Where(w => employees.Contains(w.EmployeeId)).ToListAsync();
        Assert.All(balances, w => Assert.Equal(100m, w.Balance));
    }

    private static async Task Eventually(Func<Task<int>> read, int expected)
    {
        var deadline = DateTime.UtcNow + Patience;
        var last = int.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            last = await read();
            if (last >= expected)
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Expected {expected} but the last reading was {last} after {Patience.TotalSeconds:0}s.");
    }

    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly OutboxPublisher<RewardsDbContext> _publisher;
        private readonly WelcomeBonusConsumer _consumer;
        private bool _consumerStarted;

        private Pipeline(OutboxPublisher<RewardsDbContext> publisher, WelcomeBonusConsumer consumer)
        {
            _publisher = publisher;
            _consumer = consumer;
        }

        public static async Task<Pipeline> StartAsync(ChaosStack stack, string topic, bool startConsumer = true)
        {
            var scopes = stack.Services.GetRequiredService<IServiceScopeFactory>();

            var publisher = new OutboxPublisher<RewardsDbContext>(
                scopes,
                Options.Create(new OutboxPublisherOptions
                {
                    BootstrapServers = stack.BootstrapServers,
                    Topic = topic,
                    PollInterval = TimeSpan.FromMilliseconds(200),
                }),
                NullLogger<OutboxPublisher<RewardsDbContext>>.Instance);

            var consumer = new WelcomeBonusConsumer(
                scopes,
                Options.Create(new WelcomeBonusConsumerOptions
                {
                    BootstrapServers = stack.BootstrapServers,
                    Topics = [topic],
                    GroupId = $"chaos-{Guid.NewGuid():N}",
                }),
                NullLogger<WelcomeBonusConsumer>.Instance);

            await publisher.StartAsync(CancellationToken.None);
            var pipeline = new Pipeline(publisher, consumer);
            if (startConsumer)
            {
                await pipeline.StartConsumerAsync();
            }

            return pipeline;
        }

        public async Task StartConsumerAsync()
        {
            _consumerStarted = true;
            await _consumer.StartAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _publisher.StopAsync(CancellationToken.None);
            if (_consumerStarted)
            {
                await _consumer.StopAsync(CancellationToken.None);
            }

            _publisher.Dispose();
            _consumer.Dispose();
        }
    }
}
