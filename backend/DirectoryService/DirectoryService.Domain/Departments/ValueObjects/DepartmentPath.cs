using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Departments.ValueObjects;

public partial record DepartmentPath
{
    private const string _deleted = "deleted-";
    private const char _separator = '.';
    public string Value { get; private set; }

    private DepartmentPath(string value)
    {
        Value = value;
    }

    public static Result<DepartmentPath, Error> CreateParent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Department path is required");

        string trimmed = value.Trim();

        if (!LatinDotHyphenRegex().IsMatch(trimmed))
        {
            return Error.Validation(null!, "Department path is invalid");
        }

        DepartmentPath result = new(trimmed);

        return Result.Success<DepartmentPath, Error>(result);
    }

    public static Result<DepartmentPath, Error> CreateChild(string value, DepartmentPath parentPath)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Department path is required");

        string trimmed = value.Trim();

        if (!LatinDotHyphenRegex().IsMatch(trimmed))
        {
            return Error.Validation(null!, "Department path is invalid");
        }

        string childPath = parentPath.Value + _separator + trimmed;

        DepartmentPath result = new(childPath);

        return Result.Success<DepartmentPath, Error>(result);
    }

    public static Result<DepartmentPath, Error> Create(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Error.Validation(null!, "Department path is invalid");
        }

        string trimmed = value.Trim();

        if (!LatinDotHyphenRegex().IsMatch(trimmed))
        {
            return Error.Validation(null!, "Department path is invalid");
        }

        DepartmentPath result = new(trimmed);

        return Result.Success<DepartmentPath, Error>(result);
    }

    public DepartmentPath ChangePath()
    {
        var segments = Value.Split(_separator);
        var lastSegment = segments[^1];
        if (lastSegment.StartsWith(_deleted, StringComparison.OrdinalIgnoreCase))
        {
            return new DepartmentPath(Value);
        }

        segments[^1] = _deleted + lastSegment;
        var newPathValue = string.Join(_separator, segments);

        return new DepartmentPath(newPathValue);
    }

    [GeneratedRegex(@"^[a-zA-Z.-]+$")]
    private static partial Regex LatinDotHyphenRegex();
}