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

        // Валидация входных данных
        _logger.LogInformation("Validating department");
        ValidationResult validateResult = await _validator.ValidateAsync(locationRequest, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate location111");

            return validateResult.ToError();
        }

        var locationNameResult = LocationName.Create(locationRequest.Name);
        if (locationNameResult.IsFailure)
        {
            _logger.LogError("Failed to create location name");
            return locationNameResult.Error;
        }

        LocationName locationName = locationNameResult.Value;

        var locationAddressResult = Address.Create(
            locationRequest.Address.Street,
            locationRequest.Address.City,
            locationRequest.Address.Country);
        if (locationAddressResult.IsFailure)
        {
            _logger.LogError("Failed to create location address");
            return locationAddressResult.Error;
        }

        Address locationAddress = locationAddressResult.Value;
        var locationTimezoneResult = Timezone.Create(locationRequest.Timezone);
        if (locationTimezoneResult.IsFailure)
        {
            _logger.LogError("Failed to create location timezone");
            return locationTimezoneResult.Error;
        }

        Timezone locationTimezone = locationTimezoneResult.Value;

        Locations locations = new Locations(locationId, locationName, locationTimezone, locationAddress,
            new List<DepartmentLocation>());

        var result = await _locationsRepository.Add(locations, cancellationToken);
        _logger.LogInformation("Location created successfully");

        if (result.IsFailure)
        {
            _logger.LogError("Location created fail");
            return result.Error;
        }

        return result;
    }
}