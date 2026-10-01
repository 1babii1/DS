using CSharpFunctionalExtensions;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Application.IntegrationEvents;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Department.Commands;

public record RenameDepartmentCommand(Guid DepartmentId, RenameDepartmentRequest Request);

public class RenameDepartmentValidation : AbstractValidator<RenameDepartmentCommand>
{
    public RenameDepartmentValidation()
    {
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("departmentId cannot be empty");
        RuleFor(x => x.Request.Name).MustBeValueObject(name => DepartmentName.Create(name));
    }
}

// Only the name changes: the identifier and the path are built from the identifier, so a department keeps its place
// and its address. The change and the DepartmentRenamed event are one transaction; a rename to the name it already
// has is a success that publishes nothing, so no index is told to rebuild for no reason.
public class RenameDepartmentHandler(
    IDepartmentRepository departmentRepository,
    RenameDepartmentValidation validator,
    ITransactionManager transactionManager,
    ILogger<RenameDepartmentHandler> logger,
    HybridCache cache,
    IOutboxWriter outboxWriter)
{
    public async Task<Result<DepartmentId, Error>> Handle(
        RenameDepartmentCommand command,
        CancellationToken cancellationToken)
    {
        ValidationResult validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            logger.LogWarning("Invalid rename request for department {DepartmentId}", command.DepartmentId);
            return validation.ToError();
        }

        var name = DepartmentName.Create(command.Request.Name).Value;
        var departmentId = DepartmentId.FromValue(command.DepartmentId);

        var transactionScopeResult = await transactionManager.BeginTransactionAsync(cancellationToken);
        if (transactionScopeResult.IsFailure)
        {
            return transactionScopeResult.Error;
        }

        // Leaving without Commit rolls back on Dispose.
        await using var transactionScope = transactionScopeResult.Value;

        var found = await departmentRepository.GetByIdWithLock(departmentId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var department = found.Value;
        if (!department.IsActive)
        {
            // A deleted department is gone as far as callers are concerned.
            return Error.NotFound("department.get", "Department not found");
        }

        if (department.Name == name)
        {
            return departmentId;
        }

        department.SetName(name);
        department.SetUpdatedAt(DateTime.UtcNow);

        outboxWriter.Enqueue(
            DepartmentEventTypes.Renamed,
            departmentId.Value.ToString(),
            new DepartmentRenamedEvent(departmentId.Value, name.Value, department.Identifier.Value));

        var save = await transactionManager.SaveChangesAsync(cancellationToken);
        if (save.IsFailure)
        {
            logger.LogError("Failed to save rename of {DepartmentId}", departmentId.Value);
            return save.Error;
        }

        var commit = await transactionScope.CommitAsync(cancellationToken);
        if (commit.IsFailure)
        {
            await transactionScope.RollbackAsync(cancellationToken);
            logger.LogError("Failed to commit rename of {DepartmentId}", departmentId.Value);
            return commit.Error;
        }

        await cache.RemoveOrIgnoreAsync(logger, key: GetKey.DepartmentKey.ById(departmentId), cancellationToken);
        if (department.ParentId is { } parentId)
        {
            await cache.RemoveOrIgnoreAsync(
                logger, key: GetKey.DepartmentKey.Children(parentId.Value), cancellationToken);
        }

        return departmentId;
    }
}
