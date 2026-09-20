using CSharpFunctionalExtensions;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.Departments.ValueObjects;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Application.Department.Commands;

public class UpdateDepartmentLocationsValidation : AbstractValidator<UpdateDepartmentLocationsCommand>
{
    public UpdateDepartmentLocationsValidation()
    {
        RuleFor(x => x.Request.departmentId).NotNull().NotEmpty().WithMessage("DepartmentId is not valid");
        RuleFor(x => x.Request.locationIds).NotNull().WithMessage("LocationIds is not valid");
    }
}

public record UpdateDepartmentLocationsCommand(UpdateDepartmentLocationsRequest Request);

public class UpdateDepartmentLocationsHandler
{
    private readonly IDepartmentRepository _departmentRepository;
    private readonly ILocationsRepository _locationRepository;
    private readonly UpdateDepartmentLocationsValidation _validator;
    private readonly ILogger<UpdateDepartmentLocationsHandler> _logger;
    private readonly ITransactionManager _transactionManager;
    private readonly HybridCache _cache;

    public UpdateDepartmentLocationsHandler(
        IDepartmentRepository departmentRepository,
        ILogger<UpdateDepartmentLocationsHandler> logger, ILocationsRepository locationRepository,
        UpdateDepartmentLocationsValidation validator, ITransactionManager transactionManager, HybridCache cache)
    {
        _departmentRepository = departmentRepository;
        _logger = logger;
        _locationRepository = locationRepository;
        _validator = validator;
        _transactionManager = transactionManager;
        _cache = cache;
    }

    public async Task<Result<DepartmentId, Error>> Handle(
        UpdateDepartmentLocationsCommand commandRequest,
        CancellationToken cancellationToken)
    {
        var transactionScopeResult =
            await _transactionManager.BeginTransactionAsync(cancellationToken);

        if (transactionScopeResult.IsFailure)
        {
            return transactionScopeResult.Error;
        }

        // Exiting without Commit rolls the transaction back on Dispose, so early
        // returns below don't need an explicit Rollback.
        await using var transactionScope = transactionScopeResult.Value;

        var validateResult = await _validator.ValidateAsync(commandRequest, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate update department locations");
            return validateResult.ToError();
        }

        var department = await _departmentRepository.GetByIdIncludeLocations(commandRequest.Request.departmentId, cancellationToken);
        if (department.IsFailure)
        {
            _logger.LogError("Failed to get department by id");
            return department.Error;
        }

        var locations =
            await _locationRepository.GetLocationsIds(commandRequest.Request.locationIds, cancellationToken);
        if (locations.IsFailure)
        {
            _logger.LogError("Failed to get locations by ids");
            return locations.Error;
        }

        if (locations.Value.Any())
        {
            var missed = string.Join(", ", locations.Value.Select(id => id.Value));
            _logger.LogError("Missing locations: {Missed}", missed);
            return Error.Validation("locations", $"Location not found: {missed}");
        }

        var departmentlocation =
            commandRequest.Request.locationIds.Select(id =>
                DepartmentLocation.Create(null, department.Value.Id, id).Value);

        department.Value.SetDepartmentsLocationsList(departmentlocation);

        var result = await _transactionManager.SaveChangesAsync(cancellationToken);
        if (result.IsFailure)
        {
            _logger.LogError("Failed save async update department");
            return result.Error;
        }

        var commitResult = await transactionScope.CommitAsync(cancellationToken);
        if (commitResult.IsFailure)
        {
            await transactionScope.RollbackAsync(cancellationToken);
            _logger.LogError("Failed to commit transaction");
            return commitResult.Error;
        }

        // Удаление из кэша
        await _cache.RemoveOrIgnoreAsync(
            _logger, key: GetKey.DepartmentKey.ById(department.Value.Id), cancellationToken);

        return department.Value.Id;
    }
}