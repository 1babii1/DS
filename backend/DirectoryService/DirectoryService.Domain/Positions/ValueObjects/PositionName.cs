using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Positions.ValueObjects;

public record PositionName
{
    public string Value { get; }

    [JsonConstructor]
    private PositionName(string value)
    {
        Value = value;
    }

    public static Result<PositionName, Error> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Position name is required");

        string trimmed = value.Trim();

        if (trimmed.Length is < LenghtConstants.LENGTH3 or > LenghtConstants.LENGTH100)
            return Error.Validation("length.is.invalid", "Position name must be between 3 and 100 characters");

        PositionName name = new(trimmed);

        return Result.Success<PositionName, Error>(name);
    }
}