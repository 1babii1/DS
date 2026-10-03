using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;

namespace RewardsService.IntegrationTests;

// The check behind the WalletOutOfBalance alert: a wallet's balance equals the sum of its ledger rows and of its history.
// Wallets are made the way the service makes them (through the grant writer); damage is done with plain SQL, the way a bug or a
// hand-edit would.
public class WalletInvariantTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public WalletInvariantTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task Wallets_made_by_the_service_are_in_balance()
    {
        await Grant(Guid.NewGuid(), 100);
        var busy = Guid.NewGuid();
        await Grant(busy, 10);
        await Grant(busy, 20);
        await Grant(busy, -5);

        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task A_balance_that_lost_an_update_is_reported()
    {
        var employee = Guid.NewGuid();
        await Grant(employee, 100);
        await Grant(employee, 50);

        // What the old read-modify-write did under concurrency: the ledger and the history have both grants, the balance only one.
        await Sql($"UPDATE rewards.wallets SET \"Balance\" = 100 WHERE \"EmployeeId\" = '{employee}'");

        Assert.Equal(1, await Count());
    }

    [Fact]
    public async Task A_ledger_row_with_no_event_is_reported()
    {
        var employee = Guid.NewGuid();
        await Grant(employee, 100);

        await Sql($"UPDATE rewards.transactions SET \"Amount\" = 90 WHERE \"EmployeeId\" = '{employee}'");

        Assert.Equal(1, await Count());
    }

    [Fact]
    public async Task A_history_that_disagrees_with_the_balance_is_reported()
    {
        var employee = Guid.NewGuid();
        await Grant(employee, 100);

        await Sql($"UPDATE rewards.wallet_events SET \"Data\" = jsonb_set(\"Data\", '{{{{Amount}}}}', '70') WHERE \"StreamId\" = '{employee}'");

        Assert.Equal(1, await Count());
    }

    private async Task Grant(Guid employee, decimal amount)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        new CurrencyGrantWriter(db).Grant(employee, amount, "test", Domain.TransactionSource.ManualGrant, Guid.NewGuid());
        await db.SaveChangesAsync();
    }

    private async Task Sql(string statement)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RewardsDbContext>().Database.ExecuteSqlRawAsync(statement);
    }

    private async Task<int> Count()
    {
        await using var scope = _services.CreateAsyncScope();
        return await WalletInvariantReporter.CountOutOfBalanceAsync(
            scope.ServiceProvider.GetRequiredService<RewardsDbContext>(), CancellationToken.None);
    }
}
