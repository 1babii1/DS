using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Locations.ValueObjects;

public record LocationName
{
    public string Value { get; }

    private LocationName(string value)
    {
        Value = value;
    }

    public static Result<LocationName, Error> Create(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Location name is required");

        string trimmed = value.Trim();

        if(trimmed.Length is < LenghtConstants.LENGTH3 or > LenghtConstants.LENGTH120)
            return Error.Validation("length.is.invalid", "Location name must be between 3 and 150 characters");

        LocationName name = new(trimmed);

        return Result.Success<LocationName, Error>(name);
    }
}