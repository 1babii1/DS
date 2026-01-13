using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Departments.ValueObjects;

public partial record DepartmentIdentifier
{
    public string Value { get; }

    [JsonConstructor]
    private DepartmentIdentifier(string value)
    {
        Value = value;
    }

    public static Result<DepartmentIdentifier, Error> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Department identifier is required");

        string trimmed = value.Trim();

        if (trimmed.Length is < LenghtConstants.LENGTH3 or > LenghtConstants.LENGTH150)
            return Error.Validation("length.is.invalid", "Department identifier must be between 3 and 150 characters");
        if (!LatinRegex().IsMatch(trimmed))
        {
            return Error.Validation("format.is.invalid", "Department identifier must contain only latin letters");
        }

        DepartmentIdentifier identifier = new(trimmed);

        return Result.Success<DepartmentIdentifier, Error>(identifier);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9]+$")]
    private static partial Regex LatinRegex();
}