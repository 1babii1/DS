using CSharpFunctionalExtensions;
using EmployeeService.Application.Authorization;
using EmployeeService.Application.Database;
using EmployeeService.Application.Directory;
using EmployeeService.Application.Employees.Errors;
using EmployeeService.Application.IntegrationEvents;
using Microsoft.Extensions.Logging;
using Shared;

namespace EmployeeService.Application.Employees.Commands;

public class TransferEmployeeHandler(
    IEmployeeRepository repository,
    IDepartmentAuthorization authorization,
    IDirectoryLookupClient directoryLookupClient,
    IOutboxWriter outboxWriter,
    ILogger<TransferEmployeeHandler> logger)
{
    public async Task<UnitResult<Error>> Handle(TransferEmployeeCommand command, CancellationToken cancellationToken)
    {
        var employeeResult = await repository.GetById(command.EmployeeId, cancellationToken);
        if (employeeResult.IsFailure)
        {
            return employeeResult.Error;
        }

        // Moving someone takes them out of one department and into another, so it takes the right over both (ADR 0057).
        var caller = new Caller(command.CallerAccountId, command.CallerIsAdmin);
        foreach (var departmentId in new[] { employeeResult.Value.DepartmentId, command.DepartmentId }.Distinct())
        {
            switch (await authorization.CanManageAsync(caller, departmentId, cancellationToken))
            {
                case AccessDecision.Denied:
                    return EmployeeErrors.NotDepartmentManager();
                case AccessDecision.Unavailable:
                    return EmployeeErrors.AuthorizationUnavailable();
            }
        }

        AssignmentValidationResult validation;
        try
        {
            validation = await directoryLookupClient.ValidateAssignmentAsync(
                command.DepartmentId,
                command.PositionId,
                cancellationToken);
        }
        catch (DirectoryLookupException ex) when (ex.Failure == DirectoryLookupFailure.Unauthorized)
        {
            logger.LogError(ex, "DirectoryService rejected credentials while transferring {EmployeeId}", command.EmployeeId);
            return EmployeeErrors.DirectoryUnauthorized();
        }
        catch (DirectoryLookupException ex)
        {
            logger.LogWarning(ex, "DirectoryService unavailable while transferring {EmployeeId}", command.EmployeeId);
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

        outboxWriter.Enqueue(
            EmployeeEventTypes.Transferred,
            command.EmployeeId.ToString(),
            new EmployeeTransferredEvent(command.EmployeeId, command.DepartmentId, command.PositionId));

        return await repository.Save(cancellationToken);
    }
}