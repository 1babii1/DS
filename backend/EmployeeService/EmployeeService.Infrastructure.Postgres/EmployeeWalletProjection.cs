using Microsoft.EntityFrameworkCore;

namespace EmployeeService.Infrastructure.Postgres;

/// <summary>
/// Takes a wallet change from RewardsService's events into the copy the employee card reads (ADR 0034). One statement:
/// the first event for an employee inserts the row, a later one overwrites it only if it carries a higher wallet version.
/// That makes the projection safe against everything a bus does: a redelivered event changes nothing, an older event that
/// arrives after a newer one (a replay from an earlier offset, say) cannot move the balance backwards, and two instances
/// applying events for one employee at once cannot interleave into a mix of both.
/// </summary>
public static class EmployeeWalletProjection
{
    /// <returns>True when the copy changed.</returns>
    public static async Task<bool> ApplyAsync(
        EmployeeDbContext db, Guid employeeId, decimal balance, int walletVersion, DateTime changedAt, CancellationToken cancellationToken)
    {
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO employee.employee_wallets ("EmployeeId", "Balance", "WalletVersion", "BalanceChangedAt", "ProjectedAt")
            VALUES ({employeeId}, {balance}, {walletVersion}, {changedAt}, now())
            ON CONFLICT ("EmployeeId") DO UPDATE
               SET "Balance" = EXCLUDED."Balance",
                   "WalletVersion" = EXCLUDED."WalletVersion",
                   "BalanceChangedAt" = EXCLUDED."BalanceChangedAt",
                   "ProjectedAt" = EXCLUDED."ProjectedAt"
             WHERE employee.employee_wallets."WalletVersion" < EXCLUDED."WalletVersion"
            """, cancellationToken);
        return changed == 1;
    }
}
