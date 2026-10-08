using System.Text.Json;
using EmployeeService.Application.Database;
using EmployeeService.Application.Employees;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Application.IntegrationEvents;
using EmployeeService.Domain;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Outbox;

namespace EmployeeService.IntegrationTests;

// The onboarding process against a real database (ADR 0032): it starts with the hire, follows the facts it hears about, acts when
// the deadline passes, and every effect (the saga's state, the employee's status, the compensation event) commits together.
public class HireSagaCoordinatorTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public HireSagaCoordinatorTests(EmployeeTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<Guid> HireAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>();
        var result = await handler.Handle(
            new HireEmployeeCommand($"Person {Guid.NewGuid():N}", $"p-{Guid.NewGuid():N}@test.local", Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    // The coordinator with a clock the test moves; everything else is the real thing in its own scope.
    private async Task WithCoordinator(Clock clock, Func<HireSagaCoordinator, Task> act)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await act(new HireSagaCoordinator(
            sp.GetRequiredService<IHireSagaRepository>(), sp.GetRequiredService<IEmployeeRepository>(), sp.GetRequiredService<IOutboxWriter>(), clock));
    }

    private async Task<T> Read<T>(Func<EmployeeDbContext, Task<T>> read)
    {
        await using var scope = _services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<EmployeeDbContext>());
    }

    private Task<List<HireCompensationRequestedEvent>> CompensationEvents(Guid employee) =>
        Read(async db => (await db.Set<OutboxMessage>().AsNoTracking()
                .Where(m => m.Type == EmployeeEventTypes.HireCompensationRequested && m.AggregateId == employee.ToString())
                .OrderBy(m => m.OccurredAt).ToListAsync())
            .Select(m => JsonSerializer.Deserialize<HireCompensationRequestedEvent>(m.Payload)!).ToList());

    [Fact]
    public async Task A_hire_starts_the_process_with_a_deadline_in_the_same_transaction()
    {
        var employee = await HireAsync();

        var saga = await Read(db => db.HireSagas.AsNoTracking().SingleAsync(s => s.EmployeeId == employee));

        Assert.Equal(HireSagaState.Started, saga.State);
        Assert.Equal(TimeSpan.FromMinutes(2), saga.Deadline - saga.StartedAt);
    }

    [Fact]
    public async Task The_account_and_the_bonus_in_time_complete_the_process_and_undo_nothing()
    {
        var employee = await HireAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);

        await WithCoordinator(clock, c => c.OnAccountProvisioned(employee, default));
        await WithCoordinator(clock, c => c.OnBonusGranted(employee, default));
        clock.Now = clock.Now.AddHours(1);
        await WithCoordinator(clock, c => c.CheckDeadlines(default));

        Assert.Equal(HireSagaState.Completed, (await Read(db => db.HireSagas.AsNoTracking().SingleAsync(s => s.EmployeeId == employee))).State);
        Assert.Empty(await CompensationEvents(employee));
        Assert.Equal(EmployeeStatus.PendingProvisioning, (await Read(db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee))).Status);
    }

    [Fact]
    public async Task A_missed_deadline_undoes_the_account_marks_the_employee_and_announces_it_once()
    {
        var employee = await HireAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);
        await WithCoordinator(clock, c => c.OnAccountProvisioned(employee, default));

        clock.Now = clock.Now.AddMinutes(3);
        var first = 0;
        await WithCoordinator(clock, async c => first = await c.CheckDeadlines(default));
        var second = 0;
        await WithCoordinator(clock, async c => second = await c.CheckDeadlines(default));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var announced = Assert.Single(await CompensationEvents(employee));
        Assert.True(announced.RevokeAccount);
        Assert.False(announced.ReverseBonus);
        Assert.Contains("did not complete in time", announced.Reason);
        var stored = await Read(db => db.Employees.AsNoTracking().SingleAsync(e => e.Id == employee));
        Assert.Equal(EmployeeStatus.ProvisioningFailed, stored.Status);
        Assert.Contains("did not complete in time", stored.ProvisioningFailureReason);
        Assert.Equal(HireSagaState.CompensationRequested, (await Read(db => db.HireSagas.AsNoTracking().SingleAsync(s => s.EmployeeId == employee))).State);
    }

    [Fact]
    public async Task A_bonus_that_arrives_after_the_compensation_is_announced_in_a_second_event()
    {
        var employee = await HireAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);
        await WithCoordinator(clock, c => c.OnAccountProvisioned(employee, default));
        clock.Now = clock.Now.AddMinutes(3);
        await WithCoordinator(clock, c => c.CheckDeadlines(default));

        await WithCoordinator(clock, c => c.OnBonusGranted(employee, default));
        await WithCoordinator(clock, c => c.OnBonusGranted(employee, default));

        var events = await CompensationEvents(employee);
        Assert.Equal(2, events.Count);
        Assert.True(events[1].ReverseBonus);
        Assert.False(events[1].RevokeAccount);
    }

    [Fact]
    public async Task A_failed_account_undoes_the_hire_at_once_and_a_redelivery_changes_nothing()
    {
        var employee = await HireAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);

        await WithCoordinator(clock, c => c.OnAccountProvisioningFailed(employee, "email taken", default));
        await WithCoordinator(clock, c => c.OnAccountProvisioningFailed(employee, "email taken", default));

        var announced = Assert.Single(await CompensationEvents(employee));
        Assert.False(announced.RevokeAccount);
        Assert.Contains("email taken", announced.Reason);
    }

    [Fact]
    public async Task A_fact_about_someone_this_service_never_hired_is_ignored()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);

        await WithCoordinator(clock, c => c.OnAccountProvisioned(Guid.NewGuid(), default));
        await WithCoordinator(clock, c => c.OnBonusGranted(Guid.NewGuid(), default));

        Assert.Equal(0, await Read(db => db.HireSagas.CountAsync()));
    }

    [Fact]
    public async Task Two_steps_arriving_at_once_are_not_lost_the_loser_fails_and_a_retry_applies_it()
    {
        var employee = await HireAsync();
        var clock = new Clock(DateTimeOffset.UtcNow);

        // Both read the process before either commits: the second commit must be refused, not silently overwrite the first.
        await using var a = _services.CreateAsyncScope();
        await using var b = _services.CreateAsyncScope();
        var coordinatorA = new HireSagaCoordinator(
            a.ServiceProvider.GetRequiredService<IHireSagaRepository>(), a.ServiceProvider.GetRequiredService<IEmployeeRepository>(),
            a.ServiceProvider.GetRequiredService<IOutboxWriter>(), clock);
        var sagaB = await b.ServiceProvider.GetRequiredService<IHireSagaRepository>().Get(employee, default);
        sagaB!.OnBonusGranted(DateTime.UtcNow);

        await coordinatorA.OnAccountProvisioned(employee, default);
        await Assert.ThrowsAnyAsync<DbUpdateConcurrencyException>(
            () => b.ServiceProvider.GetRequiredService<EmployeeDbContext>().SaveChangesAsync());

        // The retry, in a fresh scope, sees the account already recorded and adds the bonus: both facts end up in.
        await WithCoordinator(clock, c => c.OnBonusGranted(employee, default));
        Assert.Equal(HireSagaState.Completed, (await Read(db => db.HireSagas.AsNoTracking().SingleAsync(s => s.EmployeeId == employee))).State);
    }

    [Fact]
    public async Task Undoing_a_hire_directly_marks_the_employee_and_announces_it_whatever_the_saga_thinks()
    {
        // The Temporal workflow's activity (ADR 0056) undoes a hire through this method, so that the two orchestrators mean the same by it.
        var employee = await HireAsync();

        await WithCoordinator(
            new Clock(DateTimeOffset.UtcNow),
            coordinator => coordinator.ApplyCompensation(employee, new Compensation("a test reason", RevokeAccount: true, ReverseBonus: false), CancellationToken.None));

        Assert.Equal(EmployeeStatus.ProvisioningFailed, await Read(db => db.Set<Employee>().Where(e => e.Id == employee).Select(e => e.Status).SingleAsync()));
        var announced = Assert.Single(await CompensationEvents(employee));
        Assert.Equal("a test reason", announced.Reason);
        Assert.True(announced.RevokeAccount);
        Assert.False(announced.ReverseBonus);

        // The saga's own row is not the business of this path.
        Assert.Equal(HireSagaState.Started, await Read(db => db.Set<HireSaga>().Where(x => x.EmployeeId == employee).Select(x => x.State).SingleAsync()));
    }
}
