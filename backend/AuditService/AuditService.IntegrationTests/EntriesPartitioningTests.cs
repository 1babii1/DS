using AuditService.Domain;
using AuditService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AuditService.IntegrationTests;

// The audit log's entries table is rebuilt as a month-partitioned table by a migration EF cannot express (ADR 0027), so it is
// proven against a real Postgres, starting from a database as it was before: seeded through the old table shape, migrated,
// then checked, then migrated back. Each test has its own container, because they change the schema.
public class EntriesPartitioningTests : IAsyncLifetime
{
    private const string Before = "20261002111749_AddEntrySchemaId";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("audit_partitioning")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private AuditDbContext Context() =>
        new(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private static async Task MigrateTo(AuditDbContext db, string? target = null) =>
        await db.GetService<IMigrator>().MigrateAsync(target);

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

    // Rows as the old table held them: the unique index on MessageId existed, and nothing was partitioned.
    private async Task SeedBeforeAsync(params (Guid Message, string At)[] rows)
    {
        foreach (var (message, at) in rows)
        {
            await Execute($$"""
                INSERT INTO audit.entries ("Id", "MessageId", "SourceService", "EventType", "AggregateId", "Payload", "OccurredAt", "ReceivedAt")
                VALUES ('{{Guid.NewGuid()}}', '{{message}}', 'directory', 'DepartmentCreated', 'agg', '{"n":1}', '{{at}}', now())
                """);
        }
    }

    [Fact]
    public async Task The_migration_moves_every_row_into_partitions_and_records_every_message()
    {
        await using var db = Context();
        await MigrateTo(db, Before);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var late = Guid.NewGuid();
        await SeedBeforeAsync((first, "2026-01-15T10:00:00Z"), (second, "2026-03-05T09:00:00Z"), (late, "2026-09-30T23:59:59Z"));

        await MigrateTo(db);

        Assert.Equal(3L, await Scalar<long>("SELECT count(*) FROM audit.entries"));
        Assert.Equal(3L, await Scalar<long>("SELECT count(*) FROM audit.recorded_messages"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM audit.recorded_messages WHERE \"MessageId\" = '{first}'"));
        Assert.Equal(
            "audit.entries_y2026m01",
            await Scalar<string>("SELECT tableoid::regclass::text FROM audit.entries WHERE \"OccurredAt\" = '2026-01-15T10:00:00Z'"));
        Assert.Equal(
            "audit.entries_y2026m09",
            await Scalar<string>("SELECT tableoid::regclass::text FROM audit.entries WHERE \"OccurredAt\" = '2026-09-30T23:59:59Z'"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM audit.entries_default"));
        Assert.True(await Scalar<bool>("SELECT relkind = 'p' FROM pg_class WHERE oid = 'audit.entries'::regclass"));
    }

    [Fact]
    public async Task Partitions_exist_from_the_first_event_through_a_few_months_ahead_and_the_boundaries_are_utc()
    {
        await using var db = Context();
        await MigrateTo(db, Before);
        await SeedBeforeAsync((Guid.NewGuid(), "2026-02-10T10:00:00Z"));

        await MigrateTo(db);

        var names = await Scalar<string>(
            "SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = 'audit.entries'::regclass AND c.relname <> 'entries_default'");
        Assert.StartsWith("entries_y2026m02,entries_y2026m03", names);
        Assert.Contains($"entries_y{DateTime.UtcNow.AddMonths(3):yyyy}m{DateTime.UtcNow.AddMonths(3):MM}", names);

        // An event one second into March UTC belongs to March whatever the session's time zone is.
        await Execute("SET TIME ZONE 'Asia/Almaty'; INSERT INTO audit.entries (\"Id\", \"MessageId\", \"SourceService\", \"EventType\", \"AggregateId\", \"Payload\", \"OccurredAt\", \"ReceivedAt\") VALUES (gen_random_uuid(), gen_random_uuid(), 's', 'e', 'a', '{}', '2026-03-01T00:00:01Z', now())");
        Assert.Equal("audit.entries_y2026m03", await Scalar<string>("SELECT tableoid::regclass::text FROM audit.entries WHERE \"OccurredAt\" = '2026-03-01T00:00:01Z'"));
    }

    [Fact]
    public async Task A_message_id_is_recorded_once_even_across_partitions()
    {
        await using var db = Context();
        await MigrateTo(db);
        var message = Guid.NewGuid();

        db.RecordedMessages.Add(RecordedMessage.Create(message));
        db.Entries.Add(AuditEntry.Create(message, "directory", "DepartmentCreated", "a", "{}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();

        await using var again = Context();
        again.RecordedMessages.Add(RecordedMessage.Create(message));
        again.Entries.Add(AuditEntry.Create(message, "directory", "DepartmentCreated", "a", "{}", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM audit.entries WHERE \"MessageId\" = '{message}'"));
    }

    [Fact]
    public async Task An_event_outside_every_partition_lands_in_the_default_and_moves_when_its_month_is_created()
    {
        await using var db = Context();
        await MigrateTo(db);
        var old = new DateTime(2019, 4, 12, 8, 0, 0, DateTimeKind.Utc);
        db.RecordedMessages.Add(RecordedMessage.Create(Guid.NewGuid()));
        db.Entries.Add(AuditEntry.Create(Guid.NewGuid(), "directory", "DepartmentCreated", "a", "{}", old));
        await db.SaveChangesAsync();
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.entries_default"));

        await AuditPartitions.EnsureMonthAsync(db, old, CancellationToken.None);

        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM audit.entries_default"));
        Assert.Equal("audit.entries_y2019m04", await Scalar<string>("SELECT tableoid::regclass::text FROM audit.entries"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.entries"));

        // Idempotent: a second call changes nothing and does not fail.
        await AuditPartitions.EnsureMonthAsync(db, old, CancellationToken.None);
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.entries"));
    }

    [Fact]
    public async Task A_range_query_reads_only_the_partitions_that_can_hold_it()
    {
        await using var db = Context();
        await MigrateTo(db);
        await AuditPartitions.EnsureMonthAsync(db, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        await AuditPartitions.EnsureMonthAsync(db, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        await AuditPartitions.EnsureMonthAsync(db, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        var explain = new List<string>();
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "EXPLAIN SELECT * FROM audit.entries WHERE \"OccurredAt\" >= '2026-02-01T00:00:00Z' AND \"OccurredAt\" < '2026-03-01T00:00:00Z'", connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                explain.Add(reader.GetString(0));
            }
        }

        var text = string.Join('\n', explain);
        Assert.Contains("entries_y2026m02", text);
        Assert.DoesNotContain("entries_y2026m01", text);
        Assert.DoesNotContain("entries_y2026m03", text);
    }

    [Fact]
    public async Task Retention_drops_whole_old_partitions_and_leaves_the_recorded_message_ids()
    {
        await using var db = Context();
        await MigrateTo(db, Before);
        var oldMessage = Guid.NewGuid();
        var recentMessage = Guid.NewGuid();
        await SeedBeforeAsync((oldMessage, "2026-01-15T10:00:00Z"), (recentMessage, "2026-08-15T10:00:00Z"));
        await MigrateTo(db);

        var dropped = await AuditPartitions.DropOlderThanAsync(db, new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.Equal(["entries_y2026m01", "entries_y2026m02", "entries_y2026m03", "entries_y2026m04", "entries_y2026m05"], dropped.Order().ToList());
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM audit.entries"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM audit.recorded_messages WHERE \"MessageId\" = '{oldMessage}'"));
        Assert.Equal(1L, await Scalar<long>($"SELECT count(*) FROM audit.entries WHERE \"MessageId\" = '{recentMessage}'"));

        // June is not wholly before the cutoff, so it stays.
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM pg_class WHERE relname = 'entries_y2026m06'"));
    }

    [Fact]
    public async Task Migrating_back_restores_one_plain_table_with_every_row()
    {
        await using var db = Context();
        await MigrateTo(db, Before);
        await SeedBeforeAsync((Guid.NewGuid(), "2026-01-15T10:00:00Z"), (Guid.NewGuid(), "2026-04-02T10:00:00Z"));
        await MigrateTo(db);

        await MigrateTo(db, Before);

        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM audit.entries"));
        Assert.False(await Scalar<bool>("SELECT relkind = 'p' FROM pg_class WHERE oid = 'audit.entries'::regclass"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM pg_class WHERE relname = 'recorded_messages'"));
    }
}
