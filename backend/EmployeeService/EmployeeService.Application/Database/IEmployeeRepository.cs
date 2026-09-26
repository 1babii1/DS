using CSharpFunctionalExtensions;
using EmployeeService.Domain;
using Shared;

namespace EmployeeService.Application.Database;

public interface IEmployeeRepository
{
    Task Add(Employee employee, CancellationToken cancellationToken);

    Task<Result<Employee, Error>> GetById(Guid employeeId, CancellationToken cancellationToken);

    Task<UnitResult<Error>> Save(CancellationToken cancellationToken);
}