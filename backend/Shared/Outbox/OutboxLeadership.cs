using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Shared.Outbox;

/// <summary>
/// One publisher at a time per outbox (ADR 0055). The polling publisher reads the oldest unprocessed rows, and with several instances of a
/// service every instance read the same ones and published them all: 2.2 messages on the bus per row with three instances (ADR 0054).
/// A publishing cycle now runs inside a transaction that first takes a transaction-level advisory lock named after the outbox table; an
/// instance that does not get it skips the cycle. The lock goes when the transaction ends, committed or not, so a killed instance (its
/// connection drops) hands over at the next poll; and being transaction-level it also works through a pooler in transaction mode (ADR 0024),
/// where a session-level lock would not.
/// </summary>
public static class OutboxLeadership
{
    /// <summary>Starts the cycle's transaction and returns it if this instance is the one to publish; null when another instance is.</summary>
    public static async Task<IDbContextTransaction?> TryBeginCycleAsync(DbContext db, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var key = KeyFor(db);
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(cancellationToken);
        if (acquired)
        {
            return transaction;
        }

        await transaction.DisposeAsync();
        return null;
    }

    // The same outbox table gives the same key on every instance, and another service's table (another schema) gives another one.
    private static long KeyFor(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))
            ?? throw new InvalidOperationException($"{db.GetType().Name} has no outbox table");
        var name = $"outbox:{entity.GetSchema()}.{entity.GetTableName()}";
        return BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0);
    }
}
