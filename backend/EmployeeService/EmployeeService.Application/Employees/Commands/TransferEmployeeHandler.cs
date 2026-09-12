using CSharpFunctionalExtensions;
using EmployeeService.Application.Database;
using EmployeeService.Application.Directory;
using EmployeeService.Application.Employees.Errors;
using Microsoft.Extensions.Logging;
using Shared;

namespace EmployeeService.Application.Employees.Commands;

public class TransferEmployeeHandler(
    IEmployeeRepository repository,
    IDirectoryLookupClient directoryLookupClient,
    ILogger<TransferEmployeeHandler> logger)
{
    public async Task<UnitResult<Error>> Handle(TransferEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employeeResult = await repository.GetById(command.EmployeeId, cancellationToken);
        if (employeeResult.IsFailure)
        {
            return employeeResult.Error;
        }

        AssignmentValidationResult validation;
        try
        {
            validation = await directoryLookupClient.ValidateAssignmentAsync(
                command.DepartmentId,
                command.PositionId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DirectoryService gRPC call failed while transferring {EmployeeId}", command.EmployeeId);
            return EmployeeErrors.DirectoryUnavailable();
        }

        if (!validation.IsValid)
        {
            return !validation.DepartmentExists ? EmployeeErrors.DepartmentNotFound()
                : !validation.DepartmentActive ? EmployeeErrors.DepartmentInactive()
                : !validation.PositionExists ? EmployeeErrors.PositionNotFound()
                : !validation.PositionActive ? EmployeeErrors.PositionInactive()
                : EmployeeErrors.PositionNotInDepartment();
        }

        var transferResult = employeeResult.Value.Transfer(
            command.DepartmentId,
            validation.DepartmentName,
            command.PositionId,
            validation.PositionName);

        if (transferResult.IsFailure)
        {
            return transferResult.Error;
        }

        await repository.Save(cancellationToken);

        return UnitResult.Success<Error>();
    }
}
