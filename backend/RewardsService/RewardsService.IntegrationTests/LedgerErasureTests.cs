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

// Anonymising a person's ledger (ADR 0050): the identifiers that say who it was are replaced and the free text is blanked; the amounts, the
// dates and the history of each wallet stay, so the totals the ledger exists for do not change. Against the real database.
public class LedgerErasureTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly IServiceProvider _services;
    private readonly Func<Task> _resetDatabase;

    public LedgerErasureTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task An_employees_ledger_is_rekeyed_and_blanked_but_every_amount_and_the_total_stay()
    {
        var employee = Guid.NewGuid();
        var other = Guid.NewGuid();
        Grant(employee, 100, "Welcome bonus for Ann Example", TransactionSource.WelcomeBonus, null);
        Grant(employee, 40, "Thanks Ann for the talk", TransactionSource.ManualGrant, Guid.NewGuid());
        Grant(other, 7, "someone else", TransactionSource.ManualGrant, null);
        var totalBefore = await Total();

        var result = await Erase(employee.ToString());

        Assert.Equal(1, result.Wallets);
        Assert.Equal(2, result.Transactions);
        Assert.Equal(totalBefore, await Total());
        Assert.Equal(0, await Query(db => db.Transactions.CountAsync(t => t.EmployeeId == employee)));
        Assert.Equal(0, await Query(db => db.WalletEvents.CountAsync(e => e.StreamId == employee)));
        Assert.Equal(0, await Query(db => db.Wallets.CountAsync(w => w.EmployeeId == employee)));
        Assert.Equal(0, await Query(db => db.Transactions.CountAsync(t => t.Reason.Contains("Ann"))));
        Assert.DoesNotContain(await Query(db => db.WalletEvents.Select(e => e.Data).ToListAsync()), data => data.Contains("Ann"));
        var moved = await Query(db => db.Transactions.Where(t => t.EmployeeId != other && t.Reason == "[erased]").Select(t => t.Amount).OrderBy(a => a).ToListAsync());
        Assert.Equal([40m, 100m], moved);
        Assert.Equal(7, await Query(db => db.Wallets.Where(w => w.EmployeeId == other).Select(w => w.Balance).SingleAsync()));
    }

    [Fact]
    public async Task The_history_still_folds_to_the_same_balance_and_a_rebuild_gives_back_the_same_wallets()
    {
        var employee = Guid.NewGuid();
        Grant(employee, 100, "a", TransactionSource.WelcomeBonus, null);
        Grant(employee, 25, "b", TransactionSource.ManualGrant, null);
        await Erase(employee.ToString());
        var before = await Query(db => db.Wallets.OrderBy(w => w.Balance).Select(w => w.Balance).ToListAsync());

        await using var scope = _services.CreateAsyncScope();
        await WalletProjections.RebuildAsync(scope.ServiceProvider.GetRequiredService<RewardsDbContext>(), CancellationToken.None);

        Assert.Equal(before, await Query(db => db.Wallets.OrderBy(w => w.Balance).Select(w => w.Balance).ToListAsync()));
        Assert.Equal(125, await Total());
        Assert.Equal(0, await Query(db => db.Transactions.CountAsync(t => t.EmployeeId == employee)));
    }

    [Fact]
    public async Task Erasing_a_grantor_removes_their_id_from_what_they_gave_but_not_the_amounts()
    {
        var grantor = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        Grant(recipient, 30, "thanks", TransactionSource.ManualGrant, grantor);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            db.AgentGrantUsages.Add(AgentGrantUsage.Create(grantor, DateOnly.FromDateTime(DateTime.UtcNow), 5));
            await db.SaveChangesAsync();
        }

        await Erase(grantor.ToString());

        Assert.Equal(0, await Query(db => db.Transactions.CountAsync(t => t.GrantedByAccountId == grantor)));
        Assert.DoesNotContain(await Query(db => db.WalletEvents.Select(e => e.Data).ToListAsync()), data => data.Contains(grantor.ToString()));
        Assert.Equal(0, await Query(db => db.AgentGrantUsages.CountAsync(u => u.GrantedByAccountId == grantor)));
        Assert.Equal(30, await Query(db => db.Wallets.Where(w => w.EmployeeId == recipient).Select(w => w.Balance).SingleAsync()));
    }

    [Fact]
    public async Task An_account_id_finds_the_employee_through_the_link_and_the_link_goes()
    {
        var employee = Guid.NewGuid();
        var account = Guid.NewGuid();
        Grant(employee, 100, "x", TransactionSource.WelcomeBonus, null);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            db.AccountLookups.Add(AccountLookup.Create(employee, account));
            await db.SaveChangesAsync();
        }

        var result = await Erase(account.ToString());

        Assert.Equal(1, result.Wallets);
        Assert.Equal(0, await Query(db => db.Wallets.CountAsync(w => w.EmployeeId == employee)));
        Assert.Equal(0, await Query(db => db.AccountLookups.CountAsync()));
    }

    [Fact]
    public async Task Erasing_twice_is_harmless()
    {
        var employee = Guid.NewGuid();
        Grant(employee, 100, "x", TransactionSource.WelcomeBonus, null);
        await Erase(employee.ToString());

        var again = await Erase(employee.ToString());

        Assert.Equal(0, again.Wallets);
        Assert.Equal(100, await Total());
    }

    [Fact]
    public async Task A_welcome_bonus_event_arriving_after_the_erasure_creates_nothing()
    {
        var employee = Guid.NewGuid();
        Grant(employee, 100, "x", TransactionSource.WelcomeBonus, null);
        await Erase(employee.ToString());
        var consumer = new WelcomeBonusConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WelcomeBonusConsumerOptions { WelcomeBonusAmount = 100 }),
            _services.GetRequiredService<ILogger<WelcomeBonusConsumer>>());
        var hired = new ConsumeResult<string, string>
        {
            Topic = "employee.events",
            Message = new Message<string, string>
            {
                Key = employee.ToString(),
                Value = $$"""{"EmployeeId":"{{employee}}"}""",
                Headers = new Headers
                {
                    { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
                    { "message-type", Encoding.UTF8.GetBytes("EmployeeHired") },
                },
            },
        };

        Assert.True(consumer.HandleWithRetryAndDeadLetter(hired, CancellationToken.None));

        Assert.Equal(0, await Query(db => db.Wallets.CountAsync(w => w.EmployeeId == employee)));
        Assert.Equal(100, await Total());
    }

    private void Grant(Guid employee, decimal amount, string reason, TransactionSource source, Guid? by)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        new CurrencyGrantWriter(db).Grant(employee, amount, reason, source, by);
        db.SaveChanges();
    }

    private async Task<LedgerErasureResult> Erase(string subject)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LedgerErasure>().EraseAsync([subject], CancellationToken.None);
    }

    private Task<decimal> Total() => Query(db => db.Transactions.SumAsync(t => t.Amount));

    private async Task<T> Query<T>(Func<RewardsDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<RewardsDbContext>());
    }
}
