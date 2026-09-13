using System.Text.Json;
using FluentValidation.Results;
using Shared;

namespace DirectoryService.Application.Validation;

public static class ValidationExtentions
{
    public static Error ToError(this ValidationResult validationResult) =>
        Error.Validation(validationResult.Errors.SelectMany(Unwrap));

    /// <summary>
    /// FluentValidation переносит только строку, поэтому <see cref="CustomValidators.MustBeValueObject"/>
    /// упаковывает доменную ошибку в сообщение как JSON. Здесь она распаковывается обратно, иначе код
    /// ошибки и поле теряются, а клиент получает сериализованный объект вместо текста.
    /// Обычные правила с .WithMessage("...") проходят как есть.
    /// </summary>
    private static IEnumerable<ErrorMessage> Unwrap(ValidationFailure failure)
    {
        var packed = TryUnpack(failure.ErrorMessage);
        if (packed is not null)
        {
            return packed.Messages;
        }

        var field = string.IsNullOrWhiteSpace(failure.PropertyName) ? null : failure.PropertyName;
        return [new ErrorMessage("value.is.invalid", failure.ErrorMessage, field)];
    }

    private static Error? TryUnpack(string message)
    {
        if (!message.StartsWith('{'))
        {
            return null;
        }

        try
        {
            var error = JsonSerializer.Deserialize<Error>(message);
            return error is { Messages.Count: > 0 } ? error : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}