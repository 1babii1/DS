namespace Shared;

public enum ErrorType {
    /// <summary>
    /// Нет ошибки
    /// </summary>
    NONE,

    /// <summary>
    /// Ошибка с валидацией.
    /// </summary>
    VALIDATION,

    /// <summary>
    /// Ошибка ничего не найдено.
    /// </summary>
    NOT_FOUND,

    /// <summary>
    /// Ошибка сервера.
    /// </summary>
    FAILURE,

    /// <summary>
    /// Ошибка конфликт.
    /// </summary>
    CONFLICT,
}