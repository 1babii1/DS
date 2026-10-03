using EmployeeService.Application.Employees.Commands;
using EmployeeService.Domain;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmployeeService.IntegrationTests;

// Erasing a person at the service that owns the record (ADR 0048): the name and address are replaced, the row stays so that ids
// held elsewhere (wallet, audit, saga) still point at something. Against the real database.
public class EraseEmployeeTests(EmployeeTestWebFactory factory) : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => factory.ResetDatabaseAsync();

    [Fact]
    public async Task A_terminated_employee_loses_the_name_and_the_address_but_the_row_stays()
    {
        var (id, email) = await HireAsync();
        await TerminateAsync(id);

        var result = await EraseAsync(id);

        Assert.True(result.IsSuccess);
        var row = await ReadAsync(id);
        Assert.NotNull(row);
        Assert.Equal(EmployeeStatus.Terminated, row.Status);
        Assert.DoesNotContain("Erase", row.FullName);
        Assert.DoesNotContain(email, row.Email);
        Assert.Null(row.ProvisioningFailureReason);
    }

    [Fact]
    public async Task An_active_employee_is_not_erased_it_has_to_be_let_go_first()
    {
        var (id, email) = await HireAsync();

        var result = await EraseAsync(id);

        Assert.True(result.IsFailure);
        Assert.Equal(email, (await ReadAsync(id))!.Email);
    }

    [Fact]
    public async Task Erasing_twice_is_harmless_and_two_erased_people_do_not_collide_on_the_unique_address()
    {
        var (first, _) = await HireAsync();
        var (second, _) = await HireAsync();
        await TerminateAsync(first);
        await TerminateAsync(second);

        Assert.True((await EraseAsync(first)).IsSuccess);
        Assert.True((await EraseAsync(first)).IsSuccess);
        Assert.True((await EraseAsync(second)).IsSuccess);

        Assert.NotEqual((await ReadAsync(first))!.Email, (await ReadAsync(second))!.Email);
    }

    [Fact]
    public async Task Someone_who_does_not_exist_is_not_found()
    {
        Assert.True((await EraseAsync(Guid.NewGuid())).IsFailure);
    }

    private async Task<(Guid Id, string Email)> HireAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var email = $"erase-{Guid.NewGuid():N}@test.local";
        var result = await scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>().Handle(
            new HireEmployeeCommand("Erase Me", email, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        Assert.True(result.IsSuccess);
        return (result.Value, email);
    }

    private async Task TerminateAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<TerminateEmployeeHandler>().Handle(new TerminateEmployeeCommand(id), CancellationToken.None)).IsSuccess);
    }

    private async Task<CSharpFunctionalExtensions.UnitResult<Shared.Error>> EraseAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EraseEmployeeHandler>().Handle(new EraseEmployeeCommand(id), CancellationToken.None);
    }

    private async Task<Employee?> ReadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EmployeeDbContext>().Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
    }
}
