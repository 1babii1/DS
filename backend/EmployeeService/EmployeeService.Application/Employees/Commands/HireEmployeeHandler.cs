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
    IHireOrchestrator orchestrator,
    IIdempotencyRepository idempotency,
    TimeProvider clock,
    ILogger<HireEmployeeHandler> logger)
{
    public const string IdempotencyScope = "hire";

    public async Task<Result<Guid, Error>> Handle(HireEmployeeCommand command, CancellationToken cancellationToken)
    {
        string? requestHash = null;
        if (command.IdempotencyKey is not null)
        {
            if (command.IdempotencyKey.Length is 0 or > 200)
            {
                return EmployeeErrors.IdempotencyKeyInvalid();
            }

            // A retry of a hire that went through: answer with that hire, before doing anything else (no directory call, no write).
            requestHash = HashOf(command);
            var earlier = await idempotency.Find(IdempotencyScope, command.IdempotencyKey, cancellationToken);
            if (earlier is not null)
            {
                return earlier.RequestHash == requestHash ? earlier.ResultId : EmployeeErrors.IdempotencyKeyReused();
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

        // The onboarding process starts with the hire, in the same transaction when it keeps its state in this database (ADR 0032, 0056).
        await orchestrator.BeginAsync(employee.Id, cancellationToken);

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

        if (command.IdempotencyKey is not null)
        {
            await idempotency.Add(
                IdempotencyRecord.Create(IdempotencyScope, command.IdempotencyKey, requestHash!, employee.Id, clock.GetUtcNow().UtcDateTime),
                cancellationToken);
        }

        var saveResult = await repository.Save(cancellationToken);
        if (saveResult.IsFailure && command.IdempotencyKey is not null && LostToAnEarlierRequest(saveResult.Error))
        {
            // A request with the same key committed first, and nothing of this one was written. Which unique index reports the
            // collision first (the key's, or the email's, since a retry carries the same email) is not something to rely on, so
            // both are read as "someone got there first" and the record decides: if it is there, its result is the answer.
            idempotency.DiscardPending();
            var winner = await idempotency.Find(IdempotencyScope, command.IdempotencyKey, cancellationToken);
            if (winner is not null)
            {
                return winner.RequestHash == requestHash ? winner.ResultId : EmployeeErrors.IdempotencyKeyReused();
            }
        }

        if (saveResult.IsFailure)
        {
            return saveResult.Error;
        }

        await orchestrator.AfterCommitAsync(employee.Id, cancellationToken);

        return employee.Id;
    }

    private static bool LostToAnEarlierRequest(Error error) =>
        error.Messages[0].Code is EmployeeErrors.IdempotencyRaceCode or "employee.email.already_exists";

    // Everything the caller chose, nothing the server adds (the actor comes from the token, not the body).
    private static string HashOf(HireEmployeeCommand command)
    {
        var canonical = $"{command.FullName}|{command.Email}|{command.DepartmentId:D}|{command.PositionId:D}";
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}