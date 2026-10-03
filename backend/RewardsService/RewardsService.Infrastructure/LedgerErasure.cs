using Microsoft.EntityFrameworkCore;

namespace RewardsService.Infrastructure;

public sealed record LedgerErasureResult(int Wallets, int Transactions);

/// <summary>
/// Anonymises a person's ledger (ADR 0050) instead of deleting it: a ledger is kept for accounting, and what it is for is the amounts and
/// their dates, not who the recipient was. The wallet's history is moved to a fresh random id that is not written down anywhere (so the
/// link from the person to it is gone, not hidden), the free-text reason is blanked, and where the person was the one who granted, their
/// account id is removed from what they granted. Every amount, date and version stays, so balances and totals do not change and the
/// projections can still be rebuilt from the events. A subject may be an employee id or an account id; the link table says which is which.
/// </summary>
public sealed class LedgerErasure(RewardsDbContext db)
{
    public const string ErasedReason = "[erased]";

    public async Task<bool> IsErasedAsync(string subjectId, CancellationToken cancellationToken) =>
        await db.ErasedSubjects.AsNoTracking().AnyAsync(e => e.SubjectId == subjectId, cancellationToken);

    public async Task<LedgerErasureResult> EraseAsync(IEnumerable<string> subjects, CancellationToken cancellationToken)
    {
        var wallets = 0;
        var transactions = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Writers to the event store wait while this runs, and two erasures take turns (a plain SHARE lock would let both in and then deadlock).
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE rewards.wallet_events IN SHARE ROW EXCLUSIVE MODE", cancellationToken);

        foreach (var subject in subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        {
            var employees = new HashSet<Guid>();
            var accounts = new HashSet<Guid>();
            var marks = new HashSet<string> { subject };
            if (Guid.TryParse(subject, out var id))
            {
                employees.Add(id);
                accounts.Add(id);
                foreach (var link in await db.AccountLookups.AsNoTracking()
                             .Where(l => l.EmployeeId == id || l.AccountId == id).ToListAsync(cancellationToken))
                {
                    employees.Add(link.EmployeeId);
                    accounts.Add(link.AccountId);
                }
            }

            foreach (var employee in employees)
            {
                marks.Add(employee.ToString());
                var pseudonym = Guid.NewGuid();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE rewards.wallet_events
                    SET "StreamId" = {pseudonym}, "Data" = jsonb_set("Data", ARRAY['Reason'], to_jsonb({ErasedReason}::text))
                    WHERE "StreamId" = {employee}
                    """, cancellationToken);
                transactions += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE rewards.transactions SET "EmployeeId" = {pseudonym}, "Reason" = {ErasedReason} WHERE "EmployeeId" = {employee}
                    """, cancellationToken);
                wallets += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE rewards.wallets SET "EmployeeId" = {pseudonym} WHERE "EmployeeId" = {employee}
                    """, cancellationToken);
            }

            foreach (var account in accounts)
            {
                marks.Add(account.ToString());
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE rewards.transactions SET "GrantedByAccountId" = NULL WHERE "GrantedByAccountId" = {account}
                    """, cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE rewards.wallet_events SET "Data" = jsonb_set("Data", ARRAY['GrantedByAccountId'], 'null'::jsonb)
                    WHERE "Data" ->> 'GrantedByAccountId' = {account.ToString()}
                    """, cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM rewards.agent_grant_usage WHERE "GrantedByAccountId" = {account}
                    """, cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM rewards.account_lookups WHERE "AccountId" = {account}
                    """, cancellationToken);
            }

            foreach (var employee in employees)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM rewards.account_lookups WHERE "EmployeeId" = {employee}
                    """, cancellationToken);
            }

            foreach (var mark in marks)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO rewards.erased_subjects ("SubjectId", "ErasedAt") VALUES ({mark}, now())
                    ON CONFLICT ("SubjectId") DO NOTHING
                    """, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new LedgerErasureResult(wallets, transactions);
    }
}
