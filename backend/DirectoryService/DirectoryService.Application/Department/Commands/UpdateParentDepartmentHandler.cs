using CSharpFunctionalExtensions;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Department.Commands
{
    public record UpdateParentDepartmentCommand(Guid DepartmentId, UpdateParentDepartmentRequest Request);

    public class UpdateParentDepartmentValidation : AbstractValidator<UpdateParentDepartmentCommand>
    {
        public UpdateParentDepartmentValidation()
        {
            RuleFor(x => x.DepartmentId).NotNull().NotEmpty().WithMessage("departmentId cannot be empty");
            RuleFor(x => x.Request.parentDepartmentId).NotNull().NotEmpty().WithMessage("parentId cannot be empty");
        }
    }

    public class UpdateParentDepartmentHandler
    {
        private readonly IDepartmentRepository _departmentRepository;
        private readonly UpdateParentDepartmentValidation _validator;
        private readonly ITransactionManager _transactionManager;
        private readonly ILogger<UpdateParentDepartmentHandler> _logger;
        private readonly HybridCache _cache;

        public UpdateParentDepartmentHandler(
            IDepartmentRepository departmentRepository,
            UpdateParentDepartmentValidation validator,
            ITransactionManager transaction,
            ILogger<UpdateParentDepartmentHandler> logger,
            HybridCache cache)
        {
            _departmentRepository = departmentRepository;
            _validator = validator;
            _transactionManager = transaction;
            _logger = logger;
            _cache = cache;
        }

        public async Task<Result<DepartmentId, Error>> Handle(
            UpdateParentDepartmentCommand requestCommand,
            CancellationToken cancellationToken)
        {
            // Валидация до открытия транзакции: иначе соединение из пула занято на всё
            // время обработки запроса, который мог отсеяться на пустом идентификаторе.
            ValidationResult validateResult = await _validator.ValidateAsync(requestCommand);
            if (!validateResult.IsValid)
            {
                _logger.LogWarning("Invalid re-parent request for department {DepartmentId}", requestCommand.DepartmentId);
                return validateResult.ToError();
            }

            var transactionScopeResult =
                await _transactionManager.BeginTransactionAsync(cancellationToken);

            if (transactionScopeResult.IsFailure)
            {
                return transactionScopeResult.Error;
            }

            // Выход без Commit откатывает транзакцию при Dispose, поэтому явные Rollback
            // на каждой ветке не нужны.
            using var transactionScope = transactionScopeResult.Value;

            // Проверка id
            var newParentDepId = DepartmentId.FromValue(requestCommand.Request.parentDepartmentId);
            var currentDepId = DepartmentId.FromValue(requestCommand.DepartmentId);
            if (newParentDepId == currentDepId)
            {
                _logger.LogWarning("Department {DepartmentId} cannot be its own parent", currentDepId.Value);
                return Error.Validation("department.id.is.match", "You cannot designate yourself as a parent");
            }

            // Блокировка того с чем будем работать
            var newParentDep = await _departmentRepository.GetByIdWithLock(newParentDepId, cancellationToken);
            if (newParentDep.IsFailure)
            {
                _logger.LogWarning("New parent {ParentId} not found", newParentDepId.Value);
                return newParentDep.Error;
            }

            var currentDep = await _departmentRepository.GetByIdWithLock(currentDepId, cancellationToken);
            if (currentDep.IsFailure)
            {
                _logger.LogWarning("Department {DepartmentId} not found", currentDepId.Value);
                return currentDep.Error;
            }

            var lockChildren =
                await _departmentRepository.LockChildrenByPath(newParentDep.Value.Path, cancellationToken);
            if (lockChildren.IsFailure)
            {
                _logger.LogError(
                    "Failed to lock subtree of {ParentPath} while moving {DepartmentId}",
                    newParentDep.Value.Path.Value,
                    currentDepId.Value);
                return lockChildren.Error;
            }

            // A department cannot move into its own subtree - that would detach the subtree
            // from the tree and leave a cycle. Mirrors ltree's <@ operator: A is inside B
            // when A equals B or is prefixed by "B.".
            var newParentPath = newParentDep.Value.Path.Value;
            var currentPath = currentDep.Value.Path.Value;
            if (newParentPath == currentPath || newParentPath.StartsWith(currentPath + "."))
            {
                _logger.LogWarning(
                    "Refused to move {DepartmentId} under its own descendant {ParentId}",
                    currentDepId.Value,
                    newParentDepId.Value);
                return Error.Validation(
                    "department.cycle",
                    "A department cannot be moved under its own descendant");
            }

            var updateParent = await _departmentRepository.UpdateHierarchy(
                newParentDepId,
                newParentDep.Value.Path,
                currentDepId,
                currentDep.Value.Path,
                cancellationToken);
            if (updateParent.IsFailure)
            {
                _logger.LogError("Failed to rewrite hierarchy for {DepartmentId}", currentDepId.Value);
                return updateParent.Error;
            }

            var save = await _transactionManager.SaveChangesAsync(cancellationToken);
            if (save.IsFailure)
            {
                _logger.LogError("Failed to save re-parent of {DepartmentId}", currentDepId.Value);
                return save.Error;
            }

            var commitResult = transactionScope.Commit();
            if (commitResult.IsFailure)
            {
                transactionScope.Rollback();
                _logger.LogError("Failed to commit re-parent of {DepartmentId}", currentDepId.Value);
                return commitResult.Error;
            }

            // Удаление из кэша
            await _cache.RemoveOrIgnoreAsync(
                _logger,
                keys: GetKey.DepartmentKey.ById([newParentDepId.Value, currentDepId.Value]),
                cancellationToken);

            return newParentDepId;
        }
    }
}