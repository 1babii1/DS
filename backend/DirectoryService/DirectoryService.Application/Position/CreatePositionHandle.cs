using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using DirectoryService.Application.Department;
using DirectoryService.Application.Position.Errors;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Position;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Position;

public record CreatePositionCommand(CreatePositionRequest request);

public class CreatePositionValidation : AbstractValidator<CreatePositionCommand>
{
    public CreatePositionValidation()
    {
        RuleFor(x => x.request.Name).MustBeValueObject(name => PositionName.Create(name.Value));
        RuleFor(x => x.request.Description)
            .MustBeValueObject(description => PositionDescription.Create(description!.Value))
            .When(x => x.request.Description != null);
        RuleFor(x => x.request.DepartmentIds).NotEmpty().NotNull().WithMessage("DepartmentIds is not valid")
            .Must(list =>
            {
                IEnumerable<DepartmentId> departmentIds = list.ToList();
                return departmentIds.Select(item => item.Value).Distinct().Count() == departmentIds.Count();
            }).WithMessage("DepartmentIds contains duplicates");
    }
}

public class CreatePositionHandle
{
    private readonly IPositionRepository _positionRepository;
    private readonly IDepartmentRepository _departmentRepository;
    private readonly CreatePositionValidation _validator;
    private readonly ILogger<CreatePositionHandle> _logger;

    public CreatePositionHandle(
        IPositionRepository positionRepository,
        CreatePositionValidation validator,
        ILogger<CreatePositionHandle> logger, IDepartmentRepository departmentRepository)
    {
        _positionRepository = positionRepository;
        _validator = validator;
        _logger = logger;
        _departmentRepository = departmentRepository;
    }

    public async Task<Result<Guid, Error>> Handle(
        CreatePositionCommand positionCommand,
        CancellationToken cancellationToken)
    {
        CreatePositionRequest request = positionCommand.request;

        ValidationResult validateResult = await _validator.ValidateAsync(positionCommand, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogWarning("Invalid position request for {PositionName}", request.Name.Value);
            return validateResult.ToError();
        }

        // Проверка на существование департамента
        var departmentIdsNotFound = await _departmentRepository.GetDepartmentsIds(request.DepartmentIds, cancellationToken);
        if (departmentIdsNotFound.IsFailure)
        {
            _logger.LogError("Failed to look up departments for a new position");
            return departmentIdsNotFound.Error;
        }

        if (departmentIdsNotFound.Value.Any())
        {
            _logger.LogWarning(
                "Departments not found: {MissingDepartmentIds}",
                departmentIdsNotFound.Value.Select(id => id.Value));
            return PositionErrors.DepartmentIdsNotFound();
        }

        PositionId positionId = PositionId.NewPositionId();

        // Валидатор уже прогнал те же фабрики через MustBeValueObject.
        PositionName positionName = PositionName.Create(request.Name.Value).Value;
        PositionDescription? positionDescription = request.Description is null
            ? null
            : PositionDescription.Create(request.Description.Value).Value;

        List<DepartmentPosition> departmentPositions = [];
        foreach (var departmentId in request.DepartmentIds)
        {
            var link = DepartmentPosition.Create(null, departmentId, positionId);
            if (link.IsFailure)
            {
                _logger.LogError("Failed to link position to department {DepartmentId}", departmentId.Value);
                return Error.Failure("position.department.link", "Failed to link position to department");
            }

            departmentPositions.Add(link.Value);
        }

        Domain.Positions.Position position = new(positionId, positionName, departmentPositions,
            positionDescription);

        var result = await _positionRepository.Add(position, cancellationToken);
        if (result.IsFailure)
        {
            _logger.LogError("Failed to persist position {PositionId}", positionId.Value);
            return result.Error;
        }

        _logger.LogInformation("Position {PositionId} created", positionId.Value);
        return result;
    }
}