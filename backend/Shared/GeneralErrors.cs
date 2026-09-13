namespace Shared;

public static class GeneralErrors
{
    public static Error ValueIsInvalid(string? fieldName = null)
    {
        return fieldName == null
            ? Error.Validation("value.is.invalid", "Некорректное значение")
            : Error.Validation("value.is.invalid", $"Некорректное значение поля «{fieldName}»");
    }

    public static Error NotFound(Guid? id = null, string? entityName = null)
    {
        string entity = entityName ?? "запись";
        return Error.NotFound("record.not.found", $"Не удалось найти: {entity}");
    }

    public static Error NotFoundBy(string fieldName, string value, string? entityName = null)
    {
        string entity = entityName ?? "запись";
        return Error.NotFound("record.not.found", $"Не удалось найти {entity} по {fieldName}: «{value}»");
    }

    public static Error ValueIsRequired(string? fieldName = null)
    {
        return fieldName == null
            ? Error.Validation("value.is.required", "Обязательное поле не заполнено")
            : Error.Validation("value.is.required", $"Поле «{fieldName}» обязательно для заполнения");
    }

    public static Error LengthIsInvalid(string? fieldName = null, int? min = null, int? max = null)
    {
        string label = fieldName != null ? $"Поле «{fieldName}»" : "Поле";

        if (min.HasValue && max.HasValue)
            return Error.Validation("length.is.invalid", $"{label}: допустимая длина от {min} до {max} символов");

        if (min.HasValue)
            return Error.Validation("length.is.invalid", $"{label}: минимальная длина {min} символов");

        if (max.HasValue)
            return Error.Validation("length.is.invalid", $"{label}: максимальная длина {max} символов");

        return Error.Validation("length.is.invalid", $"{label}: недопустимая длина");
    }

    public static Error AlreadyExists(string? entityName = null, string? value = null)
    {
        string entity = entityName ?? "Запись";

        if (!string.IsNullOrEmpty(value))
            return Error.Conflict("record.already.exists", $"{entity} «{value}» уже существует");

        return Error.Conflict("record.already.exists", $"{entity} уже существует");
    }

    public static Error ConcurrencyConflict()
    {
        return Error.Conflict("concurrency.conflict", "Данные изменены другим пользователем");
    }

    public static Error UniqueConstraintViolation(string? fieldName = null)
    {
        string field = fieldName != null ? $" ({fieldName})" : string.Empty;
        return Error.Conflict("unique.constraint.violation", $"Значение{field} должно быть уникальным");
    }

    public static Error ForeignKeyViolation(string? fieldName = null)
    {
        string field = fieldName ?? "Связанная запись";
        return Error.Conflict("foreign.key.violation", $"{field} используется в других данных");
    }

    public static Error OperationCancelled()
    {
        return Error.Failure("operation.cancelled", "Операция отменена");
    }

    public static Error DatabaseError()
    {
        return Error.Failure("database.error", "Ошибка базы данных");
    }

    public static Error Failure(string? message = null)
    {
        return Error.Failure("server.failure", message ?? "Ошибка сервера");
    }

    public static Error Unauthorized()
    {
        return Error.Failure("unauthorized", "Требуется авторизация");
    }

    public static Error Forbidden()
    {
        return Error.Failure("forbidden", "Доступ запрещён");
    }

    public static Error InvalidOperation(string? message = null)
    {
        return Error.Validation("invalid.operation", message ?? "Недопустимая операция");
    }

    public static Error OutOfRange(string? fieldName = null, object? min = null, object? max = null)
    {
        string label = fieldName ?? "Значение";

        if (min != null && max != null)
            return Error.Validation("value.out.of.range", $"{label}: от {min} до {max}");

        if (min != null)
            return Error.Validation("value.out.of.range", $"{label}: минимум {min}");

        if (max != null)
            return Error.Validation("value.out.of.range", $"{label}: максимум {max}");

        return Error.Validation("value.out.of.range", $"{label} вне диапазона");
    }

    public static Error InvalidFormat(string? fieldName = null)
    {
        string label = fieldName ?? "Значение";
        return Error.Validation("invalid.format", $"{label}: неверный формат");
    }
}