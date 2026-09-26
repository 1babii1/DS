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

    /// <summary>
    /// Восстанавливает значение из хранилища без проверок. Данные уже прошли валидацию
    /// при записи, а повторная проверка на чтении означает, что любое ужесточение правила
    /// делает ранее сохранённые строки нечитаемыми: EF вызывает фабрику при материализации,
    /// и .Value на неуспешном результате бросает исключение прямо внутри запроса.
    /// Использовать только в конвертерах EF.
    /// </summary>
    public static DepartmentIdentifier FromPersisted(string value) => new(value);

    public static Result<DepartmentIdentifier, Error> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Department identifier is required");

        string trimmed = value.Trim();

        if (trimmed.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxDepartmentIdentifierLength)
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