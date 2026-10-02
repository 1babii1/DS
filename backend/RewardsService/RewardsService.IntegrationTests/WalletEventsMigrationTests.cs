using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RewardsService.Infrastructure;
using Testcontainers.PostgreSql;

namespace RewardsService.IntegrationTests;

// The migration that turns the existing ledger into the first events (ADR 0031), run against a database as it was before: a ledger
// with a wallet whose stored balance is short of it (what an overwritten update left behind).
public class WalletEventsMigrationTests : IAsyncLifetime
{
    private const string Before = "20261002134027_DropDualPublishBookkeeping";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("rewards_migration")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Existing_ledger_rows_become_the_first_events_in_order_and_balances_agree_with_them()
    {
        await using var db = new RewardsDbContext(new DbContextOptionsBuilder<RewardsDbContext>().UseNpgsql(_container.GetConnectionString()).Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);

        var employee = Guid.NewGuid();
        var other = Guid.NewGuid();
        await Execute($$"""
            INSERT INTO rewards.transactions ("Id", "EmployeeId", "Amount", "Reason", "Source", "CreatedAt")
            VALUES (gen_random_uuid(), '{{employee}}', 100, 'Welcome bonus', 'WelcomeBonus', '2026-03-01T10:00:00Z'),
                   (gen_random_uuid(), '{{employee}}', 40,  'Spot',          'ManualGrant',  '2026-03-02T10:00:00Z'),
                   (gen_random_uuid(), '{{employee}}', 10,  'Thanks',        'ManualGrant',  '2026-03-02T10:00:01Z'),
                   (gen_random_uuid(), '{{other}}',    25,  'Agent',         'AgentGrant',   '2026-03-03T10:00:00Z');
            INSERT INTO rewards.wallets ("EmployeeId", "Balance", "CreatedAt", "UpdatedAt")
            VALUES ('{{employee}}', 110, now(), now()),   -- short of its ledger (150): an overwritten update
                   ('{{other}}', 25, now(), now());
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        Assert.Equal(4L, await Scalar<long>("SELECT count(*) FROM rewards.wallet_events"));
        Assert.Equal("1,2,3", await Scalar<string>($"SELECT string_agg(\"Version\"::text, ',' ORDER BY \"Version\") FROM rewards.wallet_events WHERE \"StreamId\" = '{employee}'"));
        Assert.Equal("Welcome bonus,Spot,Thanks", await Scalar<string>($"SELECT string_agg(\"Data\" ->> 'Reason', ',' ORDER BY \"Version\") FROM rewards.wallet_events WHERE \"StreamId\" = '{employee}'"));
        Assert.Equal(150m, await Scalar<decimal>($"SELECT \"Balance\" FROM rewards.wallets WHERE \"EmployeeId\" = '{employee}'"));
        Assert.Equal(25m, await Scalar<decimal>($"SELECT \"Balance\" FROM rewards.wallets WHERE \"EmployeeId\" = '{other}'"));

        // And the rebuilt projections agree with what was there, so the migration lost nothing.
        var ledgerBefore = await Scalar<string>("SELECT string_agg(\"Id\"::text || ':' || \"Amount\"::text, ',' ORDER BY \"Id\") FROM rewards.transactions");
        await WalletProjections.RebuildAsync(db, CancellationToken.None);
        Assert.Equal(ledgerBefore, await Scalar<string>("SELECT string_agg(\"Id\"::text || ':' || \"Amount\"::text, ',' ORDER BY \"Id\") FROM rewards.transactions"));
    }
}
