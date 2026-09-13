using CSharpFunctionalExtensions;
using EmployeeService.Application.Database;
using EmployeeService.Application.Employees.Errors;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;
using Shared;
using Shared.Database;

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
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Two concurrent hires with the same email both pass validation and both
            // reach here; the unique index is what actually decides which one wins.
            // Without this, the loser hit an unhandled exception and a 500 - a retry
            // of the exact same request that would fail again the same way.
            var email = dbContext.ChangeTracker.Entries<Employee>()
                .FirstOrDefault(e => e.State == EntityState.Added)?.Entity.Email;

            return EmployeeErrors.EmailAlreadyExists(email ?? "unknown");
        }
    }
}
