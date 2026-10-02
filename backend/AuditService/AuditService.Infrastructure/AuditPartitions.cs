using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace AuditService.Infrastructure;

// Month partitions of audit.entries (ADR 0027). The table is partitioned by RANGE on "OccurredAt", the time the event
// happened. Every statement here is idempotent and safe to run on any instance at any time.
public static class AuditPartitions
{
    public const string Schema = "audit";
    public const string Parent = "entries";
    public const string Default = "entries_default";

    // DDL cannot take parameters, so these statements are built as text. What goes into them is only ever a name made of a
    // year and a month (integers) and ISO dates formatted here, never a value from a caller, which is why the interpolation
    // analyzer is silenced in these two helpers and nowhere else.
#pragma warning disable EF1002
    private static Task Run(DbContext db, string sql, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(sql, cancellationToken);

    private static IQueryable<T> Query<T>(DbContext db, string sql) =>
        db.Database.SqlQueryRaw<T>(sql);
#pragma warning restore EF1002

    public static string NameOf(DateTime month) => $"entries_y{month.Year:0000}m{month.Month:00}";

    public static DateTime MonthOf(DateTime instant) => new(instant.Year, instant.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Makes sure the partition for <paramref name="month"/> exists. Rows that already landed in the default partition for
    /// that month (an event whose time was before the first partition, or after the last created) are moved into it first:
    /// Postgres refuses to create a partition whose range the default partition already holds rows for.
    /// </summary>
    public static async Task EnsureMonthAsync(DbContext db, DateTime month, CancellationToken cancellationToken)
    {
        var from = MonthOf(month);
        var to = from.AddMonths(1);
        var name = NameOf(from);

        var bounds = (From: from.ToString("yyyy-MM-dd 00:00:00+00", CultureInfo.InvariantCulture), To: to.ToString("yyyy-MM-dd 00:00:00+00", CultureInfo.InvariantCulture));

        // One transaction: either the month exists with its rows in it, or nothing changed.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var exists = await Query<bool>(db, 
            $"SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{Schema}' AND c.relname = '{name}') AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (exists)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Built from the parent so it carries the same columns, defaults and indexes, then attached for its range.
        await Run(db, $"CREATE TABLE {Schema}.{name} (LIKE {Schema}.{Parent} INCLUDING DEFAULTS INCLUDING INDEXES INCLUDING GENERATED)", cancellationToken);
        await Run(db, 
            $"WITH moved AS (DELETE FROM {Schema}.{Default} WHERE \"OccurredAt\" >= '{bounds.From}' AND \"OccurredAt\" < '{bounds.To}' RETURNING *) INSERT INTO {Schema}.{name} SELECT * FROM moved",
            cancellationToken);
        await Run(db, 
            $"ALTER TABLE {Schema}.{Parent} ATTACH PARTITION {Schema}.{name} FOR VALUES FROM ('{bounds.From}') TO ('{bounds.To}')",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The current month and the next <paramref name="monthsAhead"/>, so events are never routed to the default partition in normal running.</summary>
    public static async Task EnsureAheadAsync(DbContext db, DateTime now, int monthsAhead, CancellationToken cancellationToken)
    {
        var first = MonthOf(now);
        for (var i = 0; i <= monthsAhead; i++)
        {
            await EnsureMonthAsync(db, first.AddMonths(i), cancellationToken);
        }
    }

    /// <summary>
    /// Drops the partitions that lie wholly before <paramref name="cutoff"/> (a month boundary). The log's history begins at its
    /// oldest partition, so this shortens what the org time machine can answer: the caller decides that, nothing does it by
    /// default.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DropOlderThanAsync(DbContext db, DateTime cutoff, CancellationToken cancellationToken)
    {
        var boundary = MonthOf(cutoff);
        var all = await Query<string>(db, 
            $"SELECT c.relname AS \"Value\" FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid JOIN pg_class p ON p.oid = i.inhparent JOIN pg_namespace n ON n.oid = p.relnamespace WHERE n.nspname = '{Schema}' AND p.relname = '{Parent}' AND c.relname LIKE 'entries_y____m__'")
            .ToListAsync(cancellationToken);

        var dropped = new List<string>();
        foreach (var name in all.Order(StringComparer.Ordinal))
        {
            var year = int.Parse(name.AsSpan(9, 4), CultureInfo.InvariantCulture);
            var month = int.Parse(name.AsSpan(14, 2), CultureInfo.InvariantCulture);
            if (new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1) <= boundary)
            {
                await Run(db, $"ALTER TABLE {Schema}.{Parent} DETACH PARTITION {Schema}.{name}", cancellationToken);
                await Run(db, $"DROP TABLE {Schema}.{name}", cancellationToken);
                dropped.Add(name);
            }
        }

        return dropped;
    }
}
