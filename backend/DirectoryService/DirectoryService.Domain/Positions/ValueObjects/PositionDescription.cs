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