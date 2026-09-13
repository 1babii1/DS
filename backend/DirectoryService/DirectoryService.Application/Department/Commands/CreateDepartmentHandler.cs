using CSharpFunctionalExtensions;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Application.Department.Errors;
using DirectoryService.Application.IntegrationEvents;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Department.Commands;

public record CreateDepartmentCommand(CreateDepartmentRequest request);

public class CreateDepartmentValidation : AbstractValidator<CreateDepartmentRequest>
{
    public CreateDepartmentValidation()
    {
        RuleFor(x => x.Name).MustBeValueObject(name => DepartmentName.Create(name.Value));
        RuleFor(x => x.Identifier).MustBeValueObject(identifier => DepartmentIdentifier.Create(identifier.Value));
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("DepartmentId is not valid")
            .When(x => x.DepartmentId != null);
        RuleFor(x => x.ParentDepartmentId).NotEmpty().WithMessage("ParentId is not valid")
            .When(x => x.ParentDepartmentId != null);
        RuleFor(x => x.LocationsIds).NotEmpty().NotNull()
            .WithMessage("DepartmentsLocationsList is not valid")
            .Must(list =>
            {
                IEnumerable<LocationId> locationIds = list.ToList();
                return locationIds.Select(item => item.Value).Distinct().Count() == locationIds.Count();
            })
            .WithMessage("DepartmentsLocationsList contains duplicates");
        RuleFor(x => x.Depth).NotEmpty().WithMessage("Depth is not valid").When(x => x.Depth != null);
    }
}

public class CreateDepartmentHandler
{
    private readonly IDepartmentRepository _departmentRepository;
    private readonly ILocationsRepository _locationRepository;
    private readonly ITransactionManager _transactionManager;
    private readonly HybridCache _cache;
    private readonly CreateDepartmentValidation _validator;
    private readonly ILogger<CreateDepartmentHandler> _logger;
    private readonly IOutboxWriter _outboxWriter;

    public CreateDepartmentHandler(IDepartmentRepository departmentRepository, CreateDepartmentValidation validator,
        ILogger<CreateDepartmentHandler> logger, ILocationsRepository locationRepository,
        ITransactionManager transactionManager, HybridCache cache, IOutboxWriter outboxWriter)
    {
        _departmentRepository = departmentRepository;
        _validator = validator;
        _logger = logger;
        _locationRepository = locationRepository;
        _transactionManager = transactionManager;
        _cache = cache;
        _outboxWriter = outboxWriter;
    }

    public async Task<Result<Guid, Error>> Handle(
        CreateDepartmentCommand createDepartmentCommandRequest,
        CancellationToken cancellationToken)
    {
        CreateDepartmentRequest request = createDepartmentCommandRequest.request;

        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(request);
        if (!validateResult.IsValid)
        {
            _logger.LogWarning("Invalid department request");
            return validateResult.ToError();
        }

        // Проверка на существование локации
        var locationIdsNotFound = await _locationRepository.GetLocationsIds(request.LocationsIds, cancellationToken);
        if (locationIdsNotFound.IsFailure)
        {
            _logger.LogError("Failed to look up locations for a new department");
            return locationIdsNotFound.Error;
        }

        // Проверка на существование Подразделения(если передан)
        Departments? departmentFromDB = null;
        if (request.ParentDepartmentId != null)
        {
            var getResult =
                await _departmentRepository.GetByIdIncludeLocations(request.ParentDepartmentId, cancellationToken);
            if (getResult.IsFailure)
            {
                _logger.LogWarning("Parent department {ParentId} not found", request.ParentDepartmentId!.Value);
                return Error.Failure("department.notfound", "Parent department not found");
            }

            departmentFromDB = getResult.Value;
        }

        if (locationIdsNotFound.Value.Any())
        {
            _logger.LogWarning("Locations not found: {MissingLocationIds}", locationIdsNotFound.Value.Select(id => id.Value));
            return DepartmentErrors.LocationsIdsNotFound();
        }

        // Валидатор уже прогнал те же фабрики через MustBeValueObject.
        DepartmentName departmentName = DepartmentName.Create(request.Name.Value).Value;
        DepartmentIdentifier departmentIdentifier = DepartmentIdentifier.Create(request.Identifier.Value).Value;

        var department = departmentFromDB is null
            ? Departments.CreateParent(departmentName, departmentIdentifier, request.LocationsIds,
                request.DepartmentId)
            : Departments.CreateChild(departmentName, departmentIdentifier, departmentFromDB,
                request.LocationsIds, request.DepartmentId);
        if (department.IsFailure)
        {
            _logger.LogWarning("Department could not be constructed from the request");
            return department.Error;
        }

        List<DepartmentLocation> departmentLocationsList = [];
        foreach (var locationIdValue in request.LocationsIds)
        {
            var departmentLocation = DepartmentLocation.Create(null, department.Value.Id, locationIdValue);
            if (departmentLocation.IsFailure)
            {
                _logger.LogError("Failed to link department to location {LocationId}", locationIdValue.Value);
                return Error.Failure("department.location.link", "Failed to link department to location");
            }

            departmentLocationsList.Add(departmentLocation.Value);
        }

        department.Value.SetDepartmentsLocationsList(departmentLocationsList);

        try
        {
            var result = await _departmentRepository.Add(department.Value, cancellationToken);
            if (result.IsFailure)
            {
                _logger.LogError("Failed to stage department {DepartmentId}", department.Value.Id.Value);
                return result.Error;
            }

            _outboxWriter.Enqueue(
                DepartmentEventTypes.Created,
                department.Value.Id.Value.ToString(),
                new DepartmentCreatedEvent(
                    department.Value.Id.Value,
                    departmentName.Value,
                    departmentIdentifier.Value,
                    departmentFromDB?.Id.Value));

            // Один коммит на департамент, его связи с локациями и запись в outbox.
            var save = await _transactionManager.SaveChangesAsync(cancellationToken);
            if (save.IsFailure)
            {
                _logger.LogError("Failed to persist department {DepartmentId}", department.Value.Id.Value);
                return save.Error;
            }

            _logger.LogInformation("Department {DepartmentId} created", department.Value.Id.Value);

            // Добавление в кэш
            await _cache.SetOrIgnoreAsync(
                _logger,
                key: GetKey.DepartmentKey.ById(department.Value.Id),
                value: department.Value,
                options: new()
                {
                    LocalCacheExpiration = TimeSpan.FromMinutes(5),
                    Expiration = TimeSpan.FromMinutes(30),
                },
                cancellationToken: cancellationToken);

            return result;
        }
        catch (Exception e)
        {
            _logger.LogError($"Failed to create department: {e}", e);
            throw;
        }
    }
}