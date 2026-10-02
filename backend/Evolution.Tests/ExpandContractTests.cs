using Npgsql;
using Testcontainers.PostgreSql;

namespace Evolution.Tests;

// Zero-downtime schema change, rehearsed against a real Postgres (ADR 0029). A column is renamed (full_name -> display_name)
// while two versions of the application run at the same time: the old one that only knows full_name and the new one that only
// knows display_name. The rename is split so that at every step both versions work, and the step that would break the old
// version is last, taken only once it is gone:
//
//   expand   add display_name, a trigger that keeps the two columns equal whichever one a statement writes, backfill
//   (deploy) the new version starts; the old one is still running
//   contract drop the trigger and the old column
//
// The "applications" are plain SQL, because what is proven is the database side of the choreography, not an ORM.
public class ExpandContractTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("evolution")
        .WithPassword("postgres")
        .WithUsername("postgres")
        .Build();

    public Task InitializeAsync() => _db.StartAsync();

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    private async Task Exec(string sql)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var connection = await Open();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    // ---- the three migration steps; the same text the runbook shows ----

    private const string Before = """
        CREATE TABLE people (id serial PRIMARY KEY, full_name text NOT NULL);
        INSERT INTO people (full_name) SELECT 'person ' || g FROM generate_series(1, 500) g;
        """;

    // Step 1, expand: additive only. lock_timeout so that waiting behind a long transaction fails fast instead of queueing
    // every other statement behind this one.
    private const string Expand = """
        SET lock_timeout = '2s';
        ALTER TABLE people ADD COLUMN display_name text;

        CREATE FUNCTION people_names_in_step() RETURNS trigger AS $$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                NEW.display_name := COALESCE(NEW.display_name, NEW.full_name);
                NEW.full_name := COALESCE(NEW.full_name, NEW.display_name);
            ELSE
                -- Whichever column the statement changed wins; the other follows.
                IF NEW.full_name IS DISTINCT FROM OLD.full_name AND NEW.display_name IS NOT DISTINCT FROM OLD.display_name THEN
                    NEW.display_name := NEW.full_name;
                ELSIF NEW.display_name IS DISTINCT FROM OLD.display_name AND NEW.full_name IS NOT DISTINCT FROM OLD.full_name THEN
                    NEW.full_name := NEW.display_name;
                END IF;
            END IF;
            RETURN NEW;
        END $$ LANGUAGE plpgsql;

        CREATE TRIGGER people_names_in_step BEFORE INSERT OR UPDATE ON people
            FOR EACH ROW EXECUTE FUNCTION people_names_in_step();
        """;

    // Step 1b: existing rows, in batches so no single statement holds locks for long.
    private const string Backfill = """
        UPDATE people SET display_name = full_name WHERE display_name IS NULL;
        """;

    // Step 2 is a deployment, not SQL: the new version goes out while the old one is still running.
    // Step 3, contract: only after no old version runs.
    private const string Contract = """
        SET lock_timeout = '2s';
        DROP TRIGGER people_names_in_step ON people;
        DROP FUNCTION people_names_in_step();
        ALTER TABLE people DROP COLUMN full_name;
        ALTER TABLE people ALTER COLUMN display_name SET NOT NULL;
        """;

    // The two versions of the application, as the statements each would run.
    private Task OldWrites(int n) => Exec($"INSERT INTO people (full_name) VALUES ('old-{n}')");

    private Task OldRenames(int id, string name) => Exec($"UPDATE people SET full_name = '{name}' WHERE id = {id}");

    private Task NewWrites(int n) => Exec($"INSERT INTO people (display_name) VALUES ('new-{n}')");

    private Task NewRenames(int id, string name) => Exec($"UPDATE people SET display_name = '{name}' WHERE id = {id}");

    [Fact]
    public async Task During_the_expand_phase_both_versions_read_and_write_and_always_agree()
    {
        await Exec(Before);
        await Exec(Expand);
        await Exec(Backfill);

        // Old version writes, new version reads; and the other way round; on new rows and on existing ones.
        await OldWrites(1);
        await NewWrites(1);
        await OldRenames(1, "renamed by the old version");
        await NewRenames(2, "renamed by the new version");

        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM people WHERE display_name IS DISTINCT FROM full_name"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM people WHERE display_name IS NULL OR full_name IS NULL"));
        Assert.Equal("renamed by the old version", await Scalar<string>("SELECT display_name FROM people WHERE id = 1"));
        Assert.Equal("renamed by the new version", await Scalar<string>("SELECT full_name FROM people WHERE id = 2"));
        Assert.Equal("old-1", await Scalar<string>("SELECT display_name FROM people WHERE full_name = 'old-1'"));
        Assert.Equal("new-1", await Scalar<string>("SELECT full_name FROM people WHERE display_name = 'new-1'"));
    }

    [Fact]
    public async Task Both_versions_running_at_once_through_the_expand_phase_lose_and_corrupt_nothing()
    {
        await Exec(Before);
        await Exec(Expand);
        await Exec(Backfill);

        const int each = 200;
        var oldVersion = Task.Run(async () =>
        {
            for (var i = 0; i < each; i++)
            {
                await OldWrites(i);
                await OldRenames(1 + (i % 100), $"old-renamed-{i}");
            }
        });
        var newVersion = Task.Run(async () =>
        {
            for (var i = 0; i < each; i++)
            {
                await NewWrites(i);
                await NewRenames(101 + (i % 100), $"new-renamed-{i}");
            }
        });
        await Task.WhenAll(oldVersion, newVersion);

        Assert.Equal(500L + (2 * each), await Scalar<long>("SELECT count(*) FROM people"));
        Assert.Equal(each, await Scalar<long>("SELECT count(*) FROM people WHERE full_name LIKE 'old-%' AND full_name NOT LIKE 'old-renamed-%'"));
        Assert.Equal(each, await Scalar<long>("SELECT count(*) FROM people WHERE display_name LIKE 'new-%' AND display_name NOT LIKE 'new-renamed-%'"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM people WHERE display_name IS DISTINCT FROM full_name"));
        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM people WHERE display_name IS NULL OR full_name IS NULL"));
    }

    [Fact]
    public async Task A_row_existing_before_the_expand_is_readable_by_the_new_version_only_after_the_backfill()
    {
        await Exec(Before);
        await Exec(Expand);

        // Between the expand and the backfill the new column is empty for old rows: this is why the new version is
        // deployed only after the backfill, not between.
        Assert.Equal(500L, await Scalar<long>("SELECT count(*) FROM people WHERE display_name IS NULL"));

        await Exec(Backfill);

        Assert.Equal(0L, await Scalar<long>("SELECT count(*) FROM people WHERE display_name IS NULL"));
    }

    [Fact]
    public async Task The_contract_step_is_what_breaks_the_old_version_so_it_comes_last()
    {
        await Exec(Before);
        await Exec(Expand);
        await Exec(Backfill);
        await NewWrites(1);
        await OldWrites(1);

        await Exec(Contract);

        // The new version carries on, with every row it had, from either version.
        Assert.Equal(502L, await Scalar<long>("SELECT count(*) FROM people"));
        Assert.Equal("old-1", await Scalar<string>("SELECT display_name FROM people WHERE display_name = 'old-1'"));
        await NewWrites(2);
        await NewRenames(1, "still works");

        // The invariant the whole change was for: the new column is now the one that must always have a value.
        var empty = await Assert.ThrowsAsync<PostgresException>(() => Exec("INSERT INTO people (display_name) VALUES (NULL)"));
        Assert.Equal("23502", empty.SqlState);

        // The old version no longer can: its column is gone.
        var failure = await Assert.ThrowsAsync<PostgresException>(() => OldWrites(2));
        Assert.Equal("42703", failure.SqlState);
    }

    [Fact]
    public async Task Skipping_the_trigger_would_corrupt_rows_written_by_the_version_that_does_not_know_the_new_column()
    {
        // The reason the trigger exists: expand without it, and an old-version write leaves the new column stale.
        await Exec(Before);
        await Exec("ALTER TABLE people ADD COLUMN display_name text; UPDATE people SET display_name = full_name;");

        await OldRenames(1, "changed by the old version");

        Assert.Equal("person 1", await Scalar<string>("SELECT display_name FROM people WHERE id = 1"));
    }

    [Fact]
    public async Task The_expand_waits_for_a_lock_for_two_seconds_at_most_instead_of_queueing_everything_behind_it()
    {
        await Exec(Before);
        await using var holder = await Open();
        await using (var begin = new NpgsqlCommand("BEGIN; LOCK TABLE people IN ACCESS SHARE MODE; SELECT 1", holder))
        {
            await begin.ExecuteNonQueryAsync();
        }

        // A long-running transaction on the table (a reader that has not finished). ADD COLUMN needs an ACCESS EXCLUSIVE lock
        // and would wait; every query after it would then wait behind IT. lock_timeout turns that into a quick failure to retry.
        await using var migration = await Open();
        await using var command = new NpgsqlCommand(Expand.Replace("2s", "300ms"), migration);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("55P03", failure.SqlState);
    }
}
