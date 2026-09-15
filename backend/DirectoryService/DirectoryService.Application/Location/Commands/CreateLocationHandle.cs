using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using DirectoryService.Application.Validation;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using Microsoft.Extensions.Logging;
using Shared;
using Address = DirectoryService.Domain.Locations.ValueObjects.Address;
using ValidationResult = FluentValidation.Results.ValidationResult;

namespace DirectoryService.Application.Location.Commands;

public class CreateLocationHandle
{
    private readonly ILocationsRepository _locationsRepository;
    private readonly CreateLocationValidation _validator;
    private readonly ILogger<CreateLocationHandle> _logger;

    public CreateLocationHandle(ILocationsRepository locationsRepository, CreateLocationValidation validator,
        ILogger<CreateLocationHandle> logger)
    {
        _locationsRepository = locationsRepository;
        _validator = validator;
        _logger = logger;
    }

    public async Task<Result<Guid, Error>> Handle(
        CreateLocationCommand createLocationCommand,
        CancellationToken cancellationToken)
    {
        LocationId locationId = LocationId.NewLocationId();
        CreateLocationRequest locationRequest = createLocationCommand.locationRequest;

        ValidationResult validateResult = await _validator.ValidateAsync(locationRequest, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogWarning("Invalid location request for {LocationName}", locationRequest.Name);
            return validateResult.ToError();
        }

        // Валидатор уже прогнал те же фабрики через MustBeValueObject, поэтому здесь
        // остаётся только собрать значения: ветки IsFailure были недостижимы.
        LocationName locationName = LocationName.Create(locationRequest.Name).Value;
        Address locationAddress = Address.Create(
            locationRequest.Address.Street,
            locationRequest.Address.City,
            locationRequest.Address.Country).Value;
        Timezone locationTimezone = Timezone.Create(locationRequest.Timezone).Value;

        Locations locations = new Locations(locationId, locationName, locationTimezone, locationAddress,
            new List<DepartmentLocation>());

        var result = await _locationsRepository.Add(locations, cancellationToken);
        if (result.IsFailure)
        {
            _logger.LogError("Failed to persist location {LocationId}", locationId.Value);
            return result.Error;
        }

        _logger.LogInformation("Location {LocationId} created", locationId.Value);
        return result;
    }
}