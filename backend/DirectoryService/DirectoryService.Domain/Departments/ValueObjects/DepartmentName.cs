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

    /// <summary>
    /// Восстанавливает значение из хранилища без проверок. Данные уже прошли валидацию
    /// при записи, а повторная проверка на чтении означает, что любое ужесточение правила
    /// делает ранее сохранённые строки нечитаемыми: EF вызывает фабрику при материализации,
    /// и .Value на неуспешном результате бросает исключение прямо внутри запроса.
    /// Использовать только в конвертерах EF.
    /// </summary>
    public static DepartmentName FromPersisted(string value) => new(value);

    public static Result<DepartmentName, Error> Create(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Department name is required");

        string trimmed = value.Trim();

        if(trimmed.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxDepartmentNameLength)
            return Error.Validation("length.is.invalid", "Department name must be between 3 and 150 characters");

        DepartmentName name = new(trimmed);

        return Result.Success<DepartmentName, Error>(name);
    }
}