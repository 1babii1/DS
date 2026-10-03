using CSharpFunctionalExtensions;
using EmployeeService.Application.Database;
using Shared;

namespace EmployeeService.Application.Employees.Commands;

/// <summary>
/// Erases the personal data of one employee at the service that owns it (ADR 0048). The copies other services keep are removed by
/// their own endpoints (docs/runbooks/erase-a-person.md); this is the record they were copied from.
/// </summary>
public class EraseEmployeeHandler(IEmployeeRepository repository)
{
    public async Task<UnitResult<Error>> Handle(EraseEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employee = await repository.GetById(command.EmployeeId, cancellationToken);
        if (employee.IsFailure)
        {
            return employee.Error;
        }

        var erased = employee.Value.Erase();
        if (erased.IsFailure)
        {
            return erased.Error;
        }

        return await repository.Save(cancellationToken);
    }
}
