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

    /// <summary>
    /// Восстанавливает значение из хранилища без проверок. Данные уже прошли валидацию
    /// при записи, а повторная проверка на чтении означает, что любое ужесточение правила
    /// делает ранее сохранённые строки нечитаемыми: EF вызывает фабрику при материализации,
    /// и .Value на неуспешном результате бросает исключение прямо внутри запроса.
    /// Использовать только в конвертерах EF.
    /// </summary>
    public static PositionDescription FromPersisted(string value) => new(value);

    public static Result<PositionDescription, Error> Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GeneralErrors.ValueIsRequired("Position description");

        string trimmed = value.Trim();

        // Было >= с сообщением про 100 символов: описание ровно в лимит отклонялось,
        // а текст ошибки называл границу, не имеющую отношения к проверке.
        if (trimmed.Length > LengthConstants.MaxPositionDescriptionLength)
            return GeneralErrors.LengthIsInvalid(
                "Position description", max: LengthConstants.MaxPositionDescriptionLength);

        PositionDescription description = new(trimmed);

        return Result.Success<PositionDescription, Error>(description);
    }
}