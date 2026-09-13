using CSharpFunctionalExtensions;
using EmployeeService.Application.Database;
using EmployeeService.Application.Employees.Errors;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;
using Shared;

namespace EmployeeService.Infrastructure.Postgres;

public class EmployeeRepository(EmployeeDbContext dbContext) : IEmployeeRepository
{
    public async Task Add(Employee employee, CancellationToken cancellationToken) =>
        await dbContext.Employees.AddAsync(employee, cancellationToken);

    public async Task<Result<Employee, Error>> GetById(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await dbContext.Employees.SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken);

        return employee is null ? EmployeeErrors.NotFound(employeeId) : employee;
    }

    public async Task<UnitResult<Error>> Save(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return UnitResult.Success<Error>();
        }
        catch (DbUpdateConcurrencyException)
        {
            // xmin no longer matches what this transaction read - someone else committed
            // a change to the same row first. The caller lost the race and needs to see
            // that as a conflict, not as an opaque 500.
            return EmployeeErrors.ConcurrencyConflict();
        }
    }
}
