using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Departments.ValueObjects;

public record DepartmentName
{
    public string Value { get; }
    [JsonConstructor]
    private DepartmentName(string value)
    {
        Value = value;
    }

    public static Result<DepartmentName, Error> Create(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Department name is required");

        string trimmed = value.Trim();

        if(trimmed.Length is < LenghtConstants.LENGTH3 or > LenghtConstants.LENGTH150)
            return Error.Validation("length.is.invalid", "Department name must be between 3 and 150 characters");

        DepartmentName name = new(trimmed);

        return Result.Success<DepartmentName, Error>(name);
    }
}