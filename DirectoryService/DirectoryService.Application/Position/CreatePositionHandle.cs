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

        // Валидация входных данных
        _logger.LogInformation("Validating department");
        ValidationResult validateResult = await _validator.ValidateAsync(positionCommand, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate location111");

            return validateResult.ToError();
        }

        // Проверка на существование департамента
        var departmentIdsNotFound = await _departmentRepository.GetDepartmentsIds(request.DepartmentIds, cancellationToken);
        if (departmentIdsNotFound.IsFailure)
        {
            _logger.LogError("Failed to get departments ids" + departmentIdsNotFound.Error.Messages);
            return departmentIdsNotFound.Error;
        }

        if (departmentIdsNotFound.Value.Any())
        {
            _logger.LogError("Departments not found" + string.Join(", ", departmentIdsNotFound.Value));
            return PositionErrors.DepartmentIdsNotFound();
        }

        PositionId positionId = PositionId.NewPositionId();

        var positionNameResult = PositionName.Create(request.Name.Value);
        if (positionNameResult.IsFailure)
        {
            _logger.LogError("Failed to create position name");
            return positionNameResult.Error;
        }

        PositionName positionName = positionNameResult.Value;

        var positionDescriptionResult = request.Description != null
            ? PositionDescription.Create(request.Description.Value)
            : Result.Success<PositionDescription, Error>(null!);

        if (positionDescriptionResult.IsFailure)
        {
            _logger.LogError("Failed to create position description");
            return positionDescriptionResult.Error;
        }

        PositionDescription? positionDescription = positionDescriptionResult.Value;

        List<DepartmentPosition> departmentPositions = new List<DepartmentPosition>();
        foreach (var departmentId in request.DepartmentIds)
        {
            var departments = DepartmentPosition.Create(null, departmentId, positionId);
            departmentPositions.Add(departments.Value);
        }

        Domain.Positions.Position position = new(positionId, positionName, departmentPositions,
            positionDescription);

        var result = await _positionRepository.Add(position, cancellationToken);

        if (result.IsFailure)
        {
            _logger.LogError("Failed to create position");
            return result.Error;
        }

        _logger.LogInformation("Position created successfully");

        return result;
    }
}