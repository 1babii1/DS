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
            var transactionScopeResult =
                await _transactionManager.BeginTransactionAsync(cancellationToken);

            if (transactionScopeResult.IsFailure)
            {
                return transactionScopeResult.Error;
            }

            using var transactionScope = transactionScopeResult.Value;

            // Валидация входных данных
            ValidationResult validateResult = await _validator.ValidateAsync(requestCommand);
            if (!validateResult.IsValid)
            {
                transactionScope.Rollback();
                _logger.LogError("Failed to validate department");
                return validateResult.ToError();
            }

            // Проверка id
            var newParentDepId = DepartmentId.FromValue(requestCommand.Request.parentDepartmentId);
            var currentDepId = DepartmentId.FromValue(requestCommand.DepartmentId);
            if (newParentDepId == currentDepId)
            {
                transactionScope.Rollback();
                _logger.LogError("department id is match");
                return Error.Validation("department.id.is.match", "You cannot designate yourself as a parent");
            }

            // Блокировка того с чем будем работать
            var newParentDep = await _departmentRepository.GetByIdWithLock(newParentDepId, cancellationToken);
            if (newParentDep.IsFailure)
            {
                transactionScope.Rollback();
                _logger.LogError("could not be found");
                return newParentDep.Error;
            }

            var currentDep = await _departmentRepository.GetByIdWithLock(currentDepId, cancellationToken);
            if (currentDep.IsFailure)
            {
                transactionScope.Rollback();
                _logger.LogError("could not be found");
                return currentDep.Error;
            }

            var lockChildren =
                await _departmentRepository.LockChildrenByPath(newParentDep.Value.Path, cancellationToken);
            if (lockChildren.IsFailure)
            {
                transactionScope.Rollback();
                _logger.LogError("could not be found");
                return currentDep.Error;
            }

            var newDepth = (short)(currentDep.Value.Depth - newParentDep.Value.Depth - 1);

            var updateParent = await _departmentRepository.UpdateHierarchy(newParentDepId, newParentDep.Value.Path,
                currentDepId,
                currentDep.Value.Path, newDepth, cancellationToken);
            if (updateParent.IsFailure)
            {
                transactionScope.Rollback();
                _logger.LogError("failed");
                return updateParent.Error;
            }

            var save = await _transactionManager.SaveChangesAsync(cancellationToken);
            if (save.IsFailure)
            {
                transactionScope.Rollback();
                _logger.LogError("failed");
                return save.Error;
            }

            transactionScope.Commit();

            // Удаление из кэша
            await _cache.RemoveAsync(
                keys: GetKey.DepartmentKey.ById([newParentDepId.Value, currentDepId.Value]), cancellationToken);

            return newParentDepId;
        }
    }
}