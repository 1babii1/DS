using EmployeeService.Application.Directory;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmployeeService.IntegrationTests;

public class HireAndTransferTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly EmployeeTestWebFactory _factory;

    private IServiceProvider Services { get; }

    public HireAndTransferTests(EmployeeTestWebFactory factory)
    {
        _factory = factory;
        Services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task Hire_with_valid_assignment_succeeds()
    {
        var result = await ExecuteHireAsync(NewHireCommand());

        Assert.True(result.IsSuccess);

        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == result.Value));
        Assert.Equal(EmployeeStatus.Active, stored.Status);
    }

    [Fact]
    public async Task Hire_with_unknown_department_fails_without_creating_a_row()
    {
        _factory.DirectoryLookup.NextValidation = FakeDirectoryLookupClient.ValidAssignment with
        {
            DepartmentExists = false,
        };

        var result = await ExecuteHireAsync(NewHireCommand());

        Assert.True(result.IsFailure);
        Assert.Equal("employee.department.not_found", result.Error.Messages[0].Code);

        var count = await ExecuteInDb(db => db.Employees.CountAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Hire_with_inactive_position_fails()
    {
        _factory.DirectoryLookup.NextValidation = FakeDirectoryLookupClient.ValidAssignment with
        {
            PositionActive = false,
        };

        var result = await ExecuteHireAsync(NewHireCommand());

        Assert.True(result.IsFailure);
        Assert.Equal("employee.position.inactive", result.Error.Messages[0].Code);
    }

    [Fact]
    public async Task Hiring_the_same_email_twice_in_sequence_returns_conflict_not_a_crash()
    {
        var email = $"dup-{Guid.NewGuid():N}@test.local";

        var first = await ExecuteHireAsync(NewHireCommand(email));
        Assert.True(first.IsSuccess);

        var second = await ExecuteHireAsync(NewHireCommand(email));

        Assert.True(second.IsFailure);
        Assert.Equal("employee.email.already_exists", second.Error.Messages[0].Code);
    }

    [Fact]
    public async Task Concurrent_hires_with_the_same_email_only_one_succeeds()
    {
        var email = $"race-{Guid.NewGuid():N}@test.local";

        var results = await Task.WhenAll(
            ExecuteHireAsync(NewHireCommand(email)),
            ExecuteHireAsync(NewHireCommand(email)));

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error.Messages[0].Code == "employee.email.already_exists");

        var count = await ExecuteInDb(db => db.Employees.CountAsync(e => e.Email == email));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Transfer_moves_the_employee_to_the_new_assignment()
    {
        var hired = await ExecuteHireAsync(NewHireCommand());
        Assert.True(hired.IsSuccess);

        var newDepartmentId = Guid.NewGuid();
        var newPositionId = Guid.NewGuid();
        _factory.DirectoryLookup.NextValidation = FakeDirectoryLookupClient.ValidAssignment with
        {
            DepartmentName = "Payments",
            PositionName = "Senior Engineer",
        };

        var transfer = await ExecuteTransferAsync(new TransferEmployeeCommand(hired.Value, newDepartmentId, newPositionId));
        Assert.True(transfer.IsSuccess);

        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == hired.Value));
        Assert.Equal(newDepartmentId, stored.DepartmentId);
        Assert.Equal(newPositionId, stored.PositionId);
        Assert.Equal("Payments", stored.DepartmentName);
    }

    [Fact]
    public async Task Transfer_of_an_unknown_employee_fails()
    {
        var result = await ExecuteTransferAsync(new TransferEmployeeCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("employee.not_found", result.Error.Messages[0].Code);
    }

    /// <summary>
    /// The concurrency-token guarantee this session's audit asked for tests on: two
    /// transfers racing on the same employee must not silently produce a lost update.
    /// One request wins, the other sees ConcurrencyConflict, and the row ends up
    /// exactly as the winner left it - not some interleaving of both writes.
    ///
    /// Racing this through the full handler via Task.WhenAll is not reliable: both
    /// calls are so fast against a local database that one can fully complete (read,
    /// write, commit) before the other even reads, which reads the fresh xmin and
    /// then also succeeds - no conflict at all, a coin flip on whether the test catches
    /// the bug it exists to catch. Staged instead: both sides read the same row first
    /// (both see the same starting xmin, guaranteed - not hoped for), and only the two
    /// SaveChanges calls actually race.
    /// </summary>
    [Fact]
    public async Task Concurrent_transfers_of_the_same_employee_one_wins_one_conflicts()
    {
        var hired = await ExecuteHireAsync(NewHireCommand());
        Assert.True(hired.IsSuccess);
        var employeeId = hired.Value;

        await using var scopeA = Services.CreateAsyncScope();
        await using var scopeB = Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<EmployeeService.Infrastructure.Postgres.EmployeeDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<EmployeeService.Infrastructure.Postgres.EmployeeDbContext>();
        var repositoryA = scopeA.ServiceProvider.GetRequiredService<EmployeeService.Application.Database.IEmployeeRepository>();
        var repositoryB = scopeB.ServiceProvider.GetRequiredService<EmployeeService.Application.Database.IEmployeeRepository>();

        var employeeA = await dbA.Employees.SingleAsync(e => e.Id == employeeId);
        var employeeB = await dbB.Employees.SingleAsync(e => e.Id == employeeId);

        var departmentA = Guid.NewGuid();
        var departmentB = Guid.NewGuid();
        Assert.True(employeeA.Transfer(departmentA, "Team A", Guid.NewGuid(), "Role A").IsSuccess);
        Assert.True(employeeB.Transfer(departmentB, "Team B", Guid.NewGuid(), "Role B").IsSuccess);

        var results = await Task.WhenAll(repositoryA.Save(CancellationToken.None), repositoryB.Save(CancellationToken.None));

        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure && r.Error.Messages[0].Code == "employee.concurrency_conflict");

        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == employeeId));
        var winningDepartment = results[0].IsSuccess ? departmentA : departmentB;
        Assert.Equal(winningDepartment, stored.DepartmentId);
    }

    [Fact]
    public async Task Terminate_of_an_active_employee_succeeds()
    {
        var hired = await ExecuteHireAsync(NewHireCommand());
        Assert.True(hired.IsSuccess);

        var result = await ExecuteTerminateAsync(new TerminateEmployeeCommand(hired.Value));

        Assert.True(result.IsSuccess);
        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == hired.Value));
        Assert.Equal(EmployeeStatus.Terminated, stored.Status);
    }

    [Fact]
    public async Task Terminating_an_already_terminated_employee_fails()
    {
        var hired = await ExecuteHireAsync(NewHireCommand());
        Assert.True(hired.IsSuccess);
        Assert.True((await ExecuteTerminateAsync(new TerminateEmployeeCommand(hired.Value))).IsSuccess);

        var second = await ExecuteTerminateAsync(new TerminateEmployeeCommand(hired.Value));

        Assert.True(second.IsFailure);
        Assert.Equal("employee.terminate.already_terminated", second.Error.Messages[0].Code);
    }

    [Fact]
    public async Task Terminate_of_an_unknown_employee_fails()
    {
        var result = await ExecuteTerminateAsync(new TerminateEmployeeCommand(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("employee.not_found", result.Error.Messages[0].Code);
    }

    /// <summary>
    /// Terminate made Status != Active reachable through the API for the first time -
    /// Transfer's own guard against moving a non-active employee existed before that
    /// and was never exercisable. Closes that gap now that it can actually happen.
    /// </summary>
    [Fact]
    public async Task Transfer_of_a_terminated_employee_fails()
    {
        var hired = await ExecuteHireAsync(NewHireCommand());
        Assert.True(hired.IsSuccess);
        Assert.True((await ExecuteTerminateAsync(new TerminateEmployeeCommand(hired.Value))).IsSuccess);

        var result = await ExecuteTransferAsync(new TransferEmployeeCommand(hired.Value, Guid.NewGuid(), Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("employee.transfer.not_active", result.Error.Messages[0].Code);

        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == hired.Value));
        Assert.Equal(EmployeeStatus.Terminated, stored.Status);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static HireEmployeeCommand NewHireCommand(string? email = null) =>
        new(
            $"Test Employee {Guid.NewGuid():N}",
            email ?? $"employee-{Guid.NewGuid():N}@test.local",
            Guid.NewGuid(),
            Guid.NewGuid());

    private async Task<CSharpFunctionalExtensions.Result<Guid, Shared.Error>> ExecuteHireAsync(HireEmployeeCommand command)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>();
        return await sut.Handle(command, CancellationToken.None);
    }

    private async Task<CSharpFunctionalExtensions.UnitResult<Shared.Error>> ExecuteTransferAsync(TransferEmployeeCommand command)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<TransferEmployeeHandler>();
        return await sut.Handle(command, CancellationToken.None);
    }

    private async Task<CSharpFunctionalExtensions.UnitResult<Shared.Error>> ExecuteTerminateAsync(TerminateEmployeeCommand command)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<TerminateEmployeeHandler>();
        return await sut.Handle(command, CancellationToken.None);
    }

    private async Task<T> ExecuteInDb<T>(Func<EmployeeService.Infrastructure.Postgres.EmployeeDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var sut = scope.ServiceProvider.GetRequiredService<EmployeeService.Infrastructure.Postgres.EmployeeDbContext>();
        return await action(sut);
    }
}