namespace EmployeeService.Application.Directory;

public enum DirectoryLookupFailure
{
    /// <summary>DirectoryService не ответил: недоступен, таймаут, разорванное соединение.</summary>
    Unavailable,

    /// <summary>DirectoryService отклонил вызов: токен не принят или прав не хватает.</summary>
    Unauthorized,
}

/// <summary>
/// Причина неудачного обращения к DirectoryService в терминах, понятных Application.
/// Нужна, чтобы отказ авторизации не выглядел как недоступность сервиса: раньше любое
/// исключение транспорта превращалось в "DirectoryService temporarily unavailable",
/// и 401 диагностировался только по логам.
/// </summary>
public class DirectoryLookupException(DirectoryLookupFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public DirectoryLookupFailure Failure { get; } = failure;
}