using System.Diagnostics;
using AuditService.Infrastructure;
using AuditService.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AuditService.IntegrationTests;

// The dev history that gives the time machine something to show is data plus a SQL emitter, and both are only worth
// having if they are coherent: this runs the real emitter against a real database and reads the result back through the
// real endpoint, at the dates the story is built around. It also holds the two properties the script relies on:
// loading twice changes nothing, and removing takes out exactly what was loaded.
public class OrgHistorySeedTests : IClassFixture<AuditTestWebFactory>, IAsyncLifetime
{
    private const int EventCount = 28;

    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public OrgHistorySeedTests(AuditTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task The_history_loads_once_however_often_it_is_loaded_and_removes_cleanly()
    {
        await Execute(Emit());
        await Execute(Emit());
        Assert.Equal(EventCount, await CountEntries());

        await Execute(Emit("--remove"));

        Assert.Equal(0, await CountEntries());
    }

    [Fact]
    public async Task Removing_takes_out_only_the_seeded_rows()
    {
        await Execute(Emit());
        await Execute("INSERT INTO audit.entries (\"Id\", \"MessageId\", \"SourceService\", \"EventType\", \"AggregateId\", \"Payload\", \"OccurredAt\", \"ReceivedAt\") " +
                      "VALUES (gen_random_uuid(), gen_random_uuid(), 'directory', 'DepartmentCreated', 'other', '{}'::jsonb, now(), now());");

        await Execute(Emit("--remove"));

        Assert.Equal(1, await CountEntries());
    }

    [Fact]
    public async Task The_story_reads_correctly_at_the_dates_it_is_built_around_and_nothing_is_unreadable()
    {
        await Execute(Emit());

        var january = await At("2026-01-10");
        Assert.Equal(["Engineering", "Payments"], january.Departments.Select(d => d.Name).ToArray());
        Assert.All(january.Departments, d => Assert.Empty(d.People));

        var afterFirstHires = await At("2026-01-12T12:00:00Z");
        Assert.Equal(
            ["Ada Lovelace/Team Lead", "Grace Hopper/Developer"],
            Dept(afterFirstHires, "Engineering").People.Select(p => $"{p.Name}/{p.Position}").ToArray());

        var march = await At("2026-03-09");
        Assert.Equal(1, Dept(march, "Platform").Depth);
        Assert.Contains(Dept(march, "Platform").People, p => p.Name == "Grace Hopper");
        Assert.DoesNotContain(Dept(march, "Engineering").People, p => p.Name == "Grace Hopper");

        Assert.Equal("Payments", (await At("2026-04-05")).Departments.Single(d => d.Identifier == "payments").Name);
        Assert.Equal("Billing", (await At("2026-04-06T09:00:00Z")).Departments.Single(d => d.Identifier == "payments").Name);

        var may = await At("2026-05-11T09:00:00Z");
        Assert.DoesNotContain(may.Departments.Single(d => d.Identifier == "payments").People, p => p.Name == "Alan Turing");
        Assert.Contains(may.Departments.Single(d => d.Identifier == "payments").People, p => p.Name == "Linus Pauling");
        Assert.Contains((await At("2026-05-11T08:59:59Z")).Departments.Single(d => d.Identifier == "payments").People, p => p.Name == "Alan Turing");

        var billing = Dept(await At("2026-06-08T09:00:00Z"), "Platform");
        Assert.Equal(1, billing.Depth);
        Assert.Equal(Dept(await At("2026-06-08T09:00:00Z"), "Billing").Id, billing.ParentId);

        Assert.Contains((await At("2026-07-12")).Departments, d => d.Identifier == "sales");
        var july = await At("2026-07-13T09:00:00Z");
        Assert.DoesNotContain(july.Departments, d => d.Identifier == "sales");
        Assert.Empty(july.Unplaced);

        var latest = await At(null);
        Assert.Equal(Dept(latest, "Engineering").Id, Dept(latest, "Platform").ParentId);
        Assert.Contains(Dept(latest, "Engineering").People, p => p.Name == "Barbara Liskov");
        Assert.Equal(0, latest.SkippedEvents);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private static OrgDepartmentView Dept(OrgChartResponse chart, string nameOrIdentifier) =>
        new(chart.Departments.Single(d => d.Name == nameOrIdentifier || d.Identifier == nameOrIdentifier.ToLowerInvariant()));

    private sealed record OrgDepartmentView(AuditService.Domain.OrgDepartment Inner)
    {
        public Guid Id => Inner.Id;

        public Guid? ParentId => Inner.ParentId;

        public int Depth => Inner.Depth;

        public IReadOnlyList<AuditService.Domain.OrgPerson> People => Inner.People;
    }

    private async Task<OrgChartResponse> At(string? at)
    {
        await using var scope = _services.CreateAsyncScope();
        var controller = new AuditController(
            scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
            TestVault.Disabled(scope.ServiceProvider.GetRequiredService<AuditDbContext>()));
        var result = await controller.OrgChart(at, CancellationToken.None);
        return result.Value ?? throw new InvalidOperationException("The endpoint refused the date.");
    }

    private async Task<int> CountEntries()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Entries.CountAsync();
    }

    // Through the raw connection: the SQL is full of JSON braces that EF's ExecuteSqlRaw would read as placeholders.
    private async Task Execute(string sql)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static string Emit(params string[] args)
    {
        var script = FindUp("scripts/org-history/emit-sql.py");
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("python3 could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"emit-sql.py failed: {error}");
        return output;
    }

    private static string FindUp(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relative} was not found above {AppContext.BaseDirectory}");
    }
}
