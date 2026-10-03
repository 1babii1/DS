using CSharpFunctionalExtensions;
using EmployeeService.Application.Database;
using EmployeeService.Application.Directory;
using EmployeeService.Application.Employees;
using EmployeeService.Application.Employees.Errors;
using EmployeeService.Application.IntegrationEvents;
using EmployeeService.Domain;
using Microsoft.Extensions.Logging;
using Shared;

namespace EmployeeService.Application.Employees.Commands;

public class HireEmployeeHandler(
    IEmployeeRepository repository,
    IDirectoryLookupClient directoryLookupClient,
    IOutboxWriter outboxWriter,
    IHireSagaRepository sagas,
    Microsoft.Extensions.Options.IOptions<HireSagaOptions> sagaOptions,
    TimeProvider clock,
    ILogger<HireEmployeeHandler> logger)
{
    public async Task<Result<Guid, Error>> Handle(HireEmployeeCommand command, CancellationToken cancellationToken)
    {
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
            logger.LogError(ex, "DirectoryService rejected credentials while hiring {Email}", command.Email);
            return EmployeeErrors.DirectoryUnauthorized();
        }
        catch (DirectoryLookupException ex)
        {
            logger.LogWarning(ex, "DirectoryService unavailable while hiring {Email}", command.Email);
            return EmployeeErrors.DirectoryUnavailable();
        }

        if (!validation.DepartmentExists)
        {
            return EmployeeErrors.DepartmentNotFound();
        }

        if (!validation.DepartmentActive)
        {
            return EmployeeErrors.DepartmentInactive();
        }

        if (!validation.PositionExists)
        {
            return EmployeeErrors.PositionNotFound();
        }

        if (!validation.PositionActive)
        {
            return EmployeeErrors.PositionInactive();
        }

        if (!validation.PositionBelongsToDepartment)
        {
            return EmployeeErrors.PositionNotInDepartment();
        }

        var employeeResult = Employee.Hire(
            command.FullName,
            command.Email,
            command.DepartmentId,
            validation.DepartmentName,
            command.PositionId,
            validation.PositionName,
            command.HiredByAccountId);

        if (employeeResult.IsFailure)
        {
            return employeeResult.Error;
        }

        var employee = employeeResult.Value;
        await repository.Add(employee, cancellationToken);

        // The onboarding process starts with the hire, in the same transaction (ADR 0032).
        await sagas.Add(HireSaga.Start(employee.Id, clock.GetUtcNow().UtcDateTime, sagaOptions.Value.OnboardingTimeout), cancellationToken);

        outboxWriter.Enqueue(
            EmployeeEventTypes.Hired,
            employee.Id.ToString(),
            new EmployeeHiredEvent(
                employee.Id,
                employee.FullName,
                employee.Email,
                employee.DepartmentId,
                employee.PositionId,
                employee.HiredByAccountId));

        var saveResult = await repository.Save(cancellationToken);
        if (saveResult.IsFailure)
        {
            return saveResult.Error;
        }

        return employee.Id;
    }
}