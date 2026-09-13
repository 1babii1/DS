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

    /// <summary>
    /// Восстанавливает значение из хранилища без проверок. Данные уже прошли валидацию
    /// при записи, а повторная проверка на чтении означает, что любое ужесточение правила
    /// делает ранее сохранённые строки нечитаемыми: EF вызывает фабрику при материализации,
    /// и .Value на неуспешном результате бросает исключение прямо внутри запроса.
    /// Использовать только в конвертерах EF.
    /// </summary>
    public static LocationName FromPersisted(string value) => new(value);

    public static Result<LocationName, Error> Create(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Location name is required");

        string trimmed = value.Trim();

        if(trimmed.Length is < LengthConstants.MinTextLength or > LengthConstants.MaxLocationNameLength)
            return Error.Validation("length.is.invalid", "Location name must be between 3 and 150 characters");

        LocationName name = new(trimmed);

        return Result.Success<LocationName, Error>(name);
    }
}