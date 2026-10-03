using EmployeeService.Application.Employees.Commands;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Outbox;

namespace EmployeeService.IntegrationTests;

// The Idempotency-Key header on a hire (ADR 0037): a retry returns the original hire, the same key with another request is
// refused, and requests racing on one key produce exactly one hire. Real handler, real Postgres.
public class IdempotentHireTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly EmployeeTestWebFactory _factory;

    public IdempotentHireTests(EmployeeTestWebFactory factory)
    {
        _factory = factory;
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _factory.DirectoryLookup.Delay = TimeSpan.Zero;
        return _resetDatabase();
    }

    [Fact]
    public async Task A_retry_with_the_same_key_returns_the_original_hire_instead_of_failing_on_its_own_email()
    {
        var command = Hire("retry@test.local", key: "k-1");

        var first = await Execute(command);
        var second = await Execute(command);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, await Count(db => db.Employees.CountAsync()));
        Assert.Equal(1, await Count(db => db.HireSagas.CountAsync()));
        Assert.Equal(1, await Count(db => db.Set<OutboxMessage>().CountAsync()));
    }

    [Fact]
    public async Task Without_a_key_a_retry_still_fails_on_the_email_as_before()
    {
        var command = Hire("nokey@test.local", key: null);

        Assert.True((await Execute(command)).IsSuccess);
        var second = await Execute(command);

        Assert.True(second.IsFailure);
        Assert.Equal("employee.email.already_exists", second.Error.Messages[0].Code);
    }

    [Fact]
    public async Task The_same_key_for_a_different_request_is_a_conflict_and_hires_nobody()
    {
        Assert.True((await Execute(Hire("one@test.local", key: "k-2"))).IsSuccess);

        var other = await Execute(Hire("two@test.local", key: "k-2"));

        Assert.True(other.IsFailure);
        Assert.Equal("employee.idempotency_key.reused", other.Error.Messages[0].Code);
        Assert.Equal(1, await Count(db => db.Employees.CountAsync()));
    }

    [Fact]
    public async Task Different_keys_are_different_hires()
    {
        var a = await Execute(Hire("a@test.local", key: "ka"));
        var b = await Execute(Hire("b@test.local", key: "kb"));

        Assert.NotEqual(a.Value, b.Value);
        Assert.Equal(2, await Count(db => db.Employees.CountAsync()));
    }

    [Fact]
    public async Task A_failed_hire_leaves_its_key_free_for_a_corrected_retry()
    {
        var bad = await Execute(Hire(string.Empty, key: "k-3"));
        Assert.True(bad.IsFailure);

        var good = await Execute(Hire("fixed@test.local", key: "k-3"));

        Assert.True(good.IsSuccess);
        Assert.Equal(0, await Count(db => db.IdempotencyRecords.CountAsync(r => r.ResultId != good.Value)));
    }

    [Fact]
    public async Task A_key_that_is_empty_or_too_long_is_refused()
    {
        var empty = await Execute(Hire("e@test.local", key: string.Empty));
        var tooLong = await Execute(Hire("l@test.local", key: new string('x', 201)));

        Assert.Equal("employee.idempotency_key.invalid", empty.Error.Messages[0].Code);
        Assert.Equal("employee.idempotency_key.invalid", tooLong.Error.Messages[0].Code);
        Assert.Equal(0, await Count(db => db.Employees.CountAsync()));
    }

    // Eight requests with one key at once: most pass the "seen this key?" read before any has committed, so the unique
    // (Scope, Key) is what decides; the losers write nothing and answer with the winner's hire.
    [Fact]
    public async Task Requests_racing_on_one_key_produce_exactly_one_hire_and_all_get_its_id()
    {
        var command = Hire("race@test.local", key: "k-race");

        // Hold every request inside the handler until all have read "no such key yet": otherwise the first often finishes
        // before the others start and the race this test is about never happens.
        _factory.DirectoryLookup.Delay = TimeSpan.FromMilliseconds(400);
        using var barrier = new Barrier(8);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await Execute(command);
        })));

        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error.GetMessage() : string.Empty));
        Assert.Single(results.Select(r => r.Value).Distinct());
        Assert.Equal(1, await Count(db => db.Employees.CountAsync()));
        Assert.Equal(1, await Count(db => db.HireSagas.CountAsync()));
        Assert.Equal(1, await Count(db => db.Set<OutboxMessage>().CountAsync()));
        Assert.Equal(1, await Count(db => db.IdempotencyRecords.CountAsync()));
    }

    private static HireEmployeeCommand Hire(string email, string? key) =>
        new(string.IsNullOrEmpty(email) ? string.Empty : "Idem Potent", email, Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), IdempotencyKey: key);

    private async Task<CSharpFunctionalExtensions.Result<Guid, Shared.Error>> Execute(HireEmployeeCommand command)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>().Handle(command, CancellationToken.None);
    }

    private async Task<T> Count<T>(Func<EmployeeDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<EmployeeDbContext>());
    }
}
