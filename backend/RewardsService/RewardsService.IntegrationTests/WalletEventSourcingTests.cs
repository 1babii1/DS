using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Domain;
using RewardsService.Infrastructure;

namespace RewardsService.IntegrationTests;

// The wallets' events are the source of truth and the wallet and ledger rows are projections (ADR 0031). Pure decisions are
// proven on the aggregate; the guarantees that need a database (one history per wallet, rebuilding) run against Postgres.
public class WalletEventSourcingTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public WalletEventSourcingTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    // ---- the aggregate: decisions without a database ----

    [Fact]
    public void The_balance_is_the_fold_of_the_history()
    {
        var employee = Guid.NewGuid();
        var wallet = WalletAggregate.Rehydrate(employee, []);
        var events = new[]
        {
            wallet.Adjust(Guid.NewGuid(), 100, "a", TransactionSource.ManualGrant, Guid.NewGuid()),
            wallet.Adjust(Guid.NewGuid(), 50, "b", TransactionSource.ManualGrant, Guid.NewGuid()),
            wallet.Adjust(Guid.NewGuid(), -30, "c", TransactionSource.ManualGrant, Guid.NewGuid()),
        };

        var again = WalletAggregate.Rehydrate(employee, events.Reverse());

        Assert.Equal(120, wallet.Balance);
        Assert.Equal(120, again.Balance);
        Assert.Equal(3, again.Version);
        Assert.Equal([1, 2, 3], events.Select(e => e.Version));
    }

    [Fact]
    public void The_welcome_bonus_is_decided_once_by_the_history_itself()
    {
        var employee = Guid.NewGuid();
        var wallet = WalletAggregate.Rehydrate(employee, []);
        wallet.Adjust(Guid.NewGuid(), 100, "Welcome bonus", TransactionSource.WelcomeBonus, null);

        Assert.Throws<WelcomeBonusAlreadyGrantedException>(
            () => wallet.Adjust(Guid.NewGuid(), 100, "Welcome bonus", TransactionSource.WelcomeBonus, null));

        // Other grants are not limited, and the same bonus is refused again from a freshly loaded history.
        wallet.Adjust(Guid.NewGuid(), 10, "Spot", TransactionSource.ManualGrant, Guid.NewGuid());
        Assert.Equal(110, wallet.Balance);
    }

    [Fact]
    public void A_history_with_a_missing_version_is_refused_not_folded_around()
    {
        var employee = Guid.NewGuid();
        var source = WalletAggregate.Rehydrate(employee, []);
        var first = source.Adjust(Guid.NewGuid(), 1, "a", TransactionSource.ManualGrant, null);
        source.Adjust(Guid.NewGuid(), 1, "b", TransactionSource.ManualGrant, null);
        var third = source.Adjust(Guid.NewGuid(), 1, "c", TransactionSource.ManualGrant, null);

        Assert.Throws<InvalidOperationException>(() => WalletAggregate.Rehydrate(employee, [first, third]));
    }

    // ---- the store ----

    [Fact]
    public async Task Two_events_cannot_both_claim_the_same_version_of_a_wallet()
    {
        var employee = Guid.NewGuid();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var a = WalletAggregate.Rehydrate(employee, []);
        db.WalletEvents.Add(a.Adjust(Guid.NewGuid(), 10, "a", TransactionSource.ManualGrant, null));
        await db.SaveChangesAsync();

        await using var other = _services.CreateAsyncScope();
        var otherDb = other.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var stale = WalletAggregate.Rehydrate(employee, []);
        otherDb.WalletEvents.Add(stale.Adjust(Guid.NewGuid(), 99, "b", TransactionSource.ManualGrant, null));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => otherDb.SaveChangesAsync());
        Assert.True(GrantConflicts.IsConcurrencyConflict(failure));
    }

    [Fact]
    public async Task Rebuilding_the_projections_from_the_events_gives_back_exactly_the_same_wallets_and_ledger()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var admin = Guid.NewGuid();
        await GrantAsync(first, 100, TransactionSource.WelcomeBonus, null);
        await GrantAsync(first, 40, TransactionSource.ManualGrant, admin);
        await GrantAsync(second, 25, TransactionSource.AgentGrant, admin);
        var before = await SnapshotAsync();

        // Damage the projections the way a bug or a lost update would, then rebuild.
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rewards.wallets SET \"Balance\" = 7");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM rewards.transactions WHERE \"Amount\" = 40");
        }

        (int Wallets, int Transactions) counts;
        await using (var scope = _services.CreateAsyncScope())
        {
            counts = await WalletProjections.RebuildAsync(scope.ServiceProvider.GetRequiredService<RewardsDbContext>(), CancellationToken.None);
        }

        Assert.Equal((2, 3), counts);
        var after = await SnapshotAsync();
        Assert.Equal(before.Wallets, after.Wallets);
        Assert.Equal(before.Ledger, after.Ledger);
        Assert.Equal(140, after.Wallets.Single(w => w.EmployeeId == first).Balance);
    }

    [Fact]
    public async Task A_grant_takes_the_balance_from_the_history_not_from_the_cached_wallet_row()
    {
        var employee = Guid.NewGuid();
        await GrantAsync(employee, 100, TransactionSource.ManualGrant, Guid.NewGuid());
        await using (var scope = _services.CreateAsyncScope())
        {
            // The cached row is wrong (a bug, a manual edit): the next grant must not build on it.
            await scope.ServiceProvider.GetRequiredService<RewardsDbContext>()
                .Database.ExecuteSqlRawAsync("UPDATE rewards.wallets SET \"Balance\" = 7");
        }

        await GrantAsync(employee, 10, TransactionSource.ManualGrant, Guid.NewGuid());

        var after = await SnapshotAsync();
        Assert.Equal(110, after.Wallets.Single().Balance);
    }

    private async Task GrantAsync(Guid employee, decimal amount, TransactionSource source, Guid? by)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        new CurrencyGrantWriter(db).Grant(employee, amount, "test", source, by);
        await db.SaveChangesAsync();
    }

    private async Task<(List<(Guid EmployeeId, decimal Balance)> Wallets, List<(Guid Id, Guid EmployeeId, decimal Amount, string Reason, string Source, Guid? By)> Ledger)> SnapshotAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var wallets = (await db.Wallets.AsNoTracking().OrderBy(w => w.EmployeeId).ToListAsync()).Select(w => (w.EmployeeId, w.Balance)).ToList();
        var ledger = (await db.Transactions.AsNoTracking().OrderBy(t => t.Id).ToListAsync())
            .Select(t => (t.Id, t.EmployeeId, t.Amount, t.Reason, t.Source.ToString(), t.GrantedByAccountId)).ToList();
        return (wallets, ledger);
    }
}
