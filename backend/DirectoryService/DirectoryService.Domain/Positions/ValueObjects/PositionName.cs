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

    /// <summary>
    /// Восстанавливает значение из хранилища без проверок. Данные уже прошли валидацию
    /// при записи, а повторная проверка на чтении означает, что любое ужесточение правила
    /// делает ранее сохранённые строки нечитаемыми: EF вызывает фабрику при материализации,
    /// и .Value на неуспешном результате бросает исключение прямо внутри запроса.
    /// Использовать только в конвертерах EF.
    /// </summary>
    public static PositionName FromPersisted(string value) => new(value);

    public static Result<PositionName, Error> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Position name is required");

        string trimmed = value.Trim();

        if (trimmed.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxPositionNameLength)
            return Error.Validation("length.is.invalid", "Position name must be between 3 and 100 characters");

        PositionName name = new(trimmed);

        return Result.Success<PositionName, Error>(name);
    }
}