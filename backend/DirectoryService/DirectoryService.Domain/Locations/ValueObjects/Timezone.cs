using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Shared;

namespace DirectoryService.Domain.Locations.ValueObjects;

public partial record Timezone
{
    public string Value { get; }

    private Timezone(string value)
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
    public static Timezone FromPersisted(string value) => new(value);

    public static Result<Timezone, Error> Create(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            return Error.Validation(null!, "Timezone is required");

        if(!TimezoneRegex().IsMatch(value))
            return Error.Validation(null!, "Timezone is invalid");

        Timezone timezone = new(value);

        return Result.Success<Timezone, Error>(timezone);
    }

    [GeneratedRegex(@"^[A-Za-z_]+\/[A-Za-z_]+$")]
    private static partial Regex TimezoneRegex();
}