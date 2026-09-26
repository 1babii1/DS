using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using RewardsService.Infrastructure.Consumers;

namespace RewardsService.IntegrationTests;

public class WelcomeBonusConsumerTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly WelcomeBonusConsumer _sut;

    public WelcomeBonusConsumerTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _sut = new WelcomeBonusConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WelcomeBonusConsumerOptions { WelcomeBonusAmount = 100 }),
            _services.GetRequiredService<ILogger<WelcomeBonusConsumer>>());
    }

    [Fact]
    public async Task EmployeeHired_grants_the_welcome_bonus_and_creates_the_wallet()
    {
        var employeeId = Guid.NewGuid();
        var result = BuildResult(Guid.NewGuid(), employeeId);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);

        var wallet = await ExecuteInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(100, wallet.Balance);

        var transaction = await ExecuteInDb(
            db => db.Transactions.SingleAsync(t => t.EmployeeId == employeeId));
        Assert.Equal(TransactionSource.WelcomeBonus, transaction.Source);
        Assert.Null(transaction.GrantedByAccountId);

        var outboxCount = await ExecuteInDb(db => db.OutboxMessages.CountAsync(m => m.AggregateId == employeeId.ToString()));
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public async Task Redelivering_the_same_EmployeeHired_event_does_not_grant_a_second_bonus()
    {
        var employeeId = Guid.NewGuid();
        var result = BuildResult(Guid.NewGuid(), employeeId);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var count = await ExecuteInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId));
        Assert.Equal(1, count);

        var wallet = await ExecuteInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(100, wallet.Balance);
    }

    [Fact]
    public async Task Concurrently_processed_EmployeeHired_events_grant_the_bonus_exactly_once()
    {
        // Sequential redelivery (the test above) only proves the second call sees the first's
        // ALREADY-COMMITTED row - it can never observe two callers racing the same
        // `Transactions.Any(...)` check before either has committed. HandleWithRetryAndDeadLetter
        // is synchronous, so genuine concurrency needs real OS threads, not just Task.WhenAll.
        var employeeId = Guid.NewGuid();
        var result = BuildResult(Guid.NewGuid(), employeeId);
        const int concurrency = 16;

        // A wallet that already exists (any employee who has received even one prior grant) has
        // no primary-key conflict left to accidentally save this race - unlike a brand-new
        // employee, where Wallet's own PK (EmployeeId) happens to reject every racer but one.
        await ExecuteInDb(async db =>
        {
            db.Wallets.Add(Wallet.Create(employeeId));
            await db.SaveChangesAsync();
            return true;
        });

        // A Barrier forces every thread to reach the race window (the "no existing bonus?" check
        // inside GrantWelcomeBonus) at the same moment, instead of trusting Task.Run's scheduling
        // to happen to overlap them - real OS threads, held at the gate until all are ready.
        using var barrier = new Barrier(concurrency);
        var threads = Enumerable.Range(0, concurrency).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);
        })).ToList();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var count = await ExecuteInDb(db => db.Transactions.CountAsync(t => t.EmployeeId == employeeId));
        Assert.Equal(1, count);

        var wallet = await ExecuteInDb(db => db.Wallets.SingleAsync(w => w.EmployeeId == employeeId));
        Assert.Equal(100, wallet.Balance);
    }

    [Fact]
    public async Task Message_of_a_different_type_on_employee_events_is_ignored()
    {
        var employeeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes("EmployeeTransferred") },
        };
        var result = new ConsumeResult<string, string>
        {
            Topic = "employee.events",
            Message = new Message<string, string> { Key = employeeId.ToString(), Value = "{}", Headers = headers },
        };

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var count = await ExecuteInDb(db => db.Transactions.CountAsync());
        Assert.Equal(0, count);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildResult(Guid messageId, Guid employeeId)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes("EmployeeHired") },
        };

        var payload = $$"""{"EmployeeId":"{{employeeId}}"}""";

        return new ConsumeResult<string, string>
        {
            Topic = "employee.events",
            Message = new Message<string, string> { Key = employeeId.ToString(), Value = payload, Headers = headers },
        };
    }

    private async Task<T> ExecuteInDb<T>(Func<RewardsDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        return await action(db);
    }
}
