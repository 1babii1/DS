using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RewardsService.Infrastructure;

/// <summary>
/// Checks, on a timer, the one property a currency ledger must never lose: a wallet's balance equals the sum of its ledger rows
/// and the sum of its history (docs/postmortems/2026-10-03-wallet-lost-updates.md). The three are written together, so a
/// disagreement means something wrote one without the others, or two writers collided. Published as a gauge; the alert is in
/// docker/prometheus/alerts.yml. Had this existed, the lost-update defect would have been visible on the day it was introduced.
/// </summary>
public sealed class WalletInvariantReporter(
    IServiceScopeFactory scopeFactory,
    ILogger<WalletInvariantReporter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Meter _meter = new("rewards-service");
    private long _outOfBalance;

    /// <summary>How many wallets have a balance that disagrees with their ledger or their history.</summary>
    public static async Task<int> CountOutOfBalanceAsync(RewardsDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value"
            FROM rewards.wallets w
            WHERE w."Balance" <> COALESCE((SELECT sum(t."Amount") FROM rewards.transactions t WHERE t."EmployeeId" = w."EmployeeId"), 0)
               OR w."Balance" <> COALESCE((SELECT sum((e."Data" ->> 'Amount')::numeric) FROM rewards.wallet_events e WHERE e."StreamId" = w."EmployeeId"), 0)
            """).ToListAsync(cancellationToken);
        return rows.Single();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.CreateObservableGauge(
            "rewards_wallets_out_of_balance", () => Interlocked.Read(ref _outOfBalance),
            description: "Wallets whose balance differs from the sum of their ledger rows or of their history; must be zero");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var count = await CountOutOfBalanceAsync(scope.ServiceProvider.GetRequiredService<RewardsDbContext>(), stoppingToken);
                Interlocked.Exchange(ref _outOfBalance, count);
                if (count > 0)
                {
                    logger.LogError("{Count} wallet(s) are out of balance with their ledger or history", count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the last known value; an unreachable database is someone else's alert.
                logger.LogWarning(ex, "Could not check the wallet invariant");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}
