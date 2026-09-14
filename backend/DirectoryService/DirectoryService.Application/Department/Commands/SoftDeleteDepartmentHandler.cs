using CSharpFunctionalExtensions;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Application.IntegrationEvents;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Department.Commands;

public class SoftDeleteDepartmentValidation : AbstractValidator<SoftDeleteDepartmentRequest>
{
    public SoftDeleteDepartmentValidation()
    {
        RuleFor(x => x.departmentId).NotNull().NotEmpty().WithMessage("DepartmentId should not be empty");
    }
}

public class SoftDeleteDepartmentHandler
{
    private readonly IDepartmentRepository _departmentRepository;
    private readonly ILocationsRepository _locationsRepository;
    private readonly IPositionRepository _positionRepository;
    private readonly ITransactionManager _transactionManager;
    private readonly ILogger<SoftDeleteDepartmentHandler> _logger;
    private readonly SoftDeleteDepartmentValidation _validation;
    private readonly HybridCache _cache;
    private readonly IOutboxWriter _outboxWriter;

    public SoftDeleteDepartmentHandler(
        IDepartmentRepository departmentRepository, ILocationsRepository locationsRepository,
        IPositionRepository positionRepository,
        ITransactionManager transactionManager, ILogger<SoftDeleteDepartmentHandler> logger,
        SoftDeleteDepartmentValidation validation, HybridCache cache, IOutboxWriter outboxWriter)
    {
        _departmentRepository = departmentRepository;
        _locationsRepository = locationsRepository;
        _positionRepository = positionRepository;
        _transactionManager = transactionManager;
        _logger = logger;
        _validation = validation;
        _cache = cache;
        _outboxWriter = outboxWriter;
    }

    public async Task<Result<DepartmentId, Error>> Handle(
        SoftDeleteDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        // Валидация до открытия транзакции: иначе соединение из пула занято на всё
        // время обработки запроса, который мог отсеяться на пустом идентификаторе.
        var validateResult = await _validation.ValidateAsync(request, cancellationToken);
        if (validateResult.IsValid == false)
        {
            _logger.LogWarning("Invalid soft delete request for department {DepartmentId}", request.departmentId);
            return validateResult.ToError();
        }

        var transaction = await _transactionManager.BeginTransactionAsync(cancellationToken);
        if (transaction.IsFailure)
        {
            return transaction.Error;
        }

        // Выход без Commit откатывает транзакцию при Dispose, поэтому явные Rollback
        // на каждой ветке не нужны.
        await using var transactionScope = transaction.Value;

        // Проверка на существование Департамента
        var department =
            await _departmentRepository.GetById(DepartmentId.FromValue(request.departmentId), cancellationToken);
        if (department.IsFailure)
        {
            _logger.LogWarning("Department {DepartmentId} not found", request.departmentId);
            return department.Error;
        }

        if (department.Value.IsActive == false)
        {
            _logger.LogWarning("Department {DepartmentId} is already deleted", request.departmentId);
            return Error.Conflict("department.already.deleted", "Department is already deleted");
        }

        // Мягкое удаление департамента
        department.Value.Delete();

        // Получение осиротевших локаций
        var locationOrphan =
            await _locationsRepository.GetOrphanLocationByDepartment(department.Value.Id, cancellationToken);
        if (locationOrphan.IsFailure)
        {
            _logger.LogError("Failed to load orphan locations for department {DepartmentId}", request.departmentId);
            return locationOrphan.Error;
        }

        if (locationOrphan.Value.Any())
        {
            foreach (var location in locationOrphan.Value)
            {
                location.Delete();
            }
        }

        // Получение осиротевших позиций
        var positionOrphan =
            await _positionRepository.GetOrphanPositionByDepartment(department.Value.Id, cancellationToken);
        if (positionOrphan.IsFailure)
        {
            _logger.LogError("Failed to load orphan positions for department {DepartmentId}", request.departmentId);
            return positionOrphan.Error;
        }

        if (positionOrphan.Value.Any())
        {
            foreach (var position in positionOrphan.Value)
            {
                position.Delete();
            }
        }

        _outboxWriter.Enqueue(
            DepartmentEventTypes.Deleted,
            department.Value.Id.Value.ToString(),
            new DepartmentDeletedEvent(department.Value.Id.Value));

        var save = await _transactionManager.SaveChangesAsync(cancellationToken);
        if (save.IsFailure)
        {
            _logger.LogError("Failed to save soft delete of department {DepartmentId}", request.departmentId);
            return save.Error;
        }

        var commitResult = await transactionScope.CommitAsync(cancellationToken);
        if (commitResult.IsFailure)
        {
            _logger.LogError("Failed to commit soft delete of department {DepartmentId}", request.departmentId);
            return commitResult.Error;
        }

        // Удаление из кэша
        await _cache.RemoveOrIgnoreAsync(
            _logger, key: GetKey.DepartmentKey.ById(department.Value.Id), cancellationToken);

        _logger.LogInformation(
            "Департамент удален{0}{1}",
            locationOrphan.Value?.Any() == true ? ", удалены связанные локации" : " ",
            positionOrphan.Value?.Any() == true ? ", удалены связанные позиции" : " ");

        return department.Value.Id;
    }
}