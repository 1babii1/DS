using Microsoft.EntityFrameworkCore;

namespace RewardsService.Infrastructure;

/// <summary>
/// The wallet rows and ledger rows are projections of the wallets' events (ADR 0031), so they can be thrown away and rebuilt
/// from them. That is what makes the events the source of truth rather than a log kept beside it: if a projection is ever wrong
/// (a bug, a manual edit, a lost update of the kind this design removed), rebuilding it is the repair.
/// </summary>
public static class WalletProjections
{
    /// <summary>
    /// Rebuilds <c>wallets</c> and <c>transactions</c> from <c>wallet_events</c> in one transaction. Appends to the event store
    /// wait while it runs (the table is locked against writes), so a grant is never half in the old projection and half in the new.
    /// Returns how many wallets and ledger rows were written.
    /// </summary>
    public static async Task<(int Wallets, int Transactions)> RebuildAsync(RewardsDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlRawAsync("LOCK TABLE rewards.wallet_events IN SHARE MODE", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM rewards.transactions", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM rewards.wallets", cancellationToken);

        var transactions = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO rewards.transactions ("Id", "EmployeeId", "Amount", "Reason", "Source", "GrantedByAccountId", "CreatedAt")
            SELECT (e."Data" ->> 'TransactionId')::uuid,
                   e."StreamId",
                   (e."Data" ->> 'Amount')::numeric(18,2),
                   e."Data" ->> 'Reason',
                   e."Data" ->> 'Source',
                   (e."Data" ->> 'GrantedByAccountId')::uuid,
                   e."OccurredAt"
            FROM rewards.wallet_events e
            WHERE e."EventType" = 'WalletAdjusted'
            """,
            cancellationToken);

        var wallets = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO rewards.wallets ("EmployeeId", "Balance", "CreatedAt", "UpdatedAt")
            SELECT e."StreamId", sum((e."Data" ->> 'Amount')::numeric(18,2)), min(e."OccurredAt"), max(e."OccurredAt")
            FROM rewards.wallet_events e
            WHERE e."EventType" = 'WalletAdjusted'
            GROUP BY e."StreamId"
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (wallets, transactions);
    }
}
