using CSharpFunctionalExtensions;
using EmployeeService.Application.Database;
using EmployeeService.Application.IntegrationEvents;
using Shared;

namespace EmployeeService.Application.Employees.Commands;

/// <summary>
/// Employee.Terminate already existed with its own guard (an already-terminated
/// employee can't be terminated again) but nothing in the application called it -
/// a dead domain method with no way to reach it from the API. Wired up now,
/// mirroring TransferEmployeeHandler's shape: no DirectoryService validation needed
/// here, since termination doesn't touch department or position assignment.
/// </summary>
public class TerminateEmployeeHandler(IEmployeeRepository repository, IOutboxWriter outboxWriter)
{
    public async Task<UnitResult<Error>> Handle(TerminateEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employeeResult = await repository.GetById(command.EmployeeId, cancellationToken);
        if (employeeResult.IsFailure)
        {
            return employeeResult.Error;
        }

        var terminateResult = employeeResult.Value.Terminate();
        if (terminateResult.IsFailure)
        {
            return terminateResult.Error;
        }

        outboxWriter.Enqueue(
            EmployeeEventTypes.Terminated,
            command.EmployeeId.ToString(),
            new EmployeeTerminatedEvent(command.EmployeeId));

        return await repository.Save(cancellationToken);
    }
}
