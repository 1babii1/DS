using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Positions.ValueObjects;

public record PositionDescription
{
    public string Value { get; }

    [JsonConstructor]
    private PositionDescription(string value)
    {
        Value = value;
    }

    public static Result<PositionDescription, Error> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Position description cannot be empty");

        string trimmed = value.Trim();

        if (trimmed.Length >= LenghtConstants.LENGTH1000)
            return Error.Validation("length.is.invalid", "Position description must be between 3 and 100 characters");

        PositionDescription description = new(trimmed);

        return Result.Success<PositionDescription, Error>(description);
    }
}