using Microsoft.EntityFrameworkCore;
using Shared.Database;

namespace RewardsService.Infrastructure;

/// <summary>
/// Two writers that both read a wallet at version N and both try to append N+1 collide on the events' primary key (or, for a
/// wallet's first grant, on the wallet row created beside it). That is not an error in either request: the loser re-reads and
/// decides again against the new state. Everything else that is unique (the welcome bonus index, an idempotency key) means
/// something different and is handled where it is understood.
/// </summary>
public static class GrantConflicts
{
    public const int MaxAttempts = 6;

    public static readonly string[] Constraints = ["PK_wallet_events", "PK_wallets"];

    public static bool IsConcurrencyConflict(DbUpdateException ex) => ex.IsUniqueViolationOf(Constraints);
}
