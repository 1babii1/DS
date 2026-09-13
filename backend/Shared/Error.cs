using System.Text.Json.Serialization;

namespace Shared;

public record ErrorMessage(string Code, string Message, string? InvalidField = null);

public record Error
{
    public IReadOnlyList<ErrorMessage> Messages { get; } = [];

    public ErrorType Type { get; }

    public bool IsCritical { get; }

    [JsonConstructor]
    private Error(IReadOnlyList<ErrorMessage> messages, ErrorType type, bool isCritical = false)
    {
        Messages = messages.ToArray();
        Type = type;
        IsCritical = isCritical;
    }

    private Error(IEnumerable<ErrorMessage> messages, ErrorType type, bool isCritical = false)
    {
        Messages = messages.ToArray();
        Type = type;
        IsCritical = isCritical;
    }

    public string GetMessage() => string.Join(";", Messages.Select(m => m.ToString()));

    public static Error Validation(string code, string message, string? invalidField = null, bool isCritical = false) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.VALIDATION, isCritical);

    public static Error NotFound(string code, string message, string? invalidField = null) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.NOT_FOUND);

    public static Error Failure(string code, string message, string? invalidField = null, bool isCritical = false) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.FAILURE, isCritical);

    public static Error Conflict(string code, string message, string? invalidField = null) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.CONFLICT);

    public static Error Authentication(string code, string message, string? invalidField = null) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.AUTHENTICATION);

    public static Error Authorization(string code, string message, string? invalidField = null) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.AUTHORIZATION);

    public static Error RateLimited(string code, string message, string? invalidField = null) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.RATE_LIMITED);

    public static Error Unprocessable(string code, string message, string? invalidField = null) =>
        new([new ErrorMessage(code, message, invalidField)], ErrorType.UNPROCESSABLE_ENTITY);

    public static Error Unprocessable(IEnumerable<ErrorMessage> messages) =>
        new(messages, ErrorType.UNPROCESSABLE_ENTITY);

    public static Error Validation(IEnumerable<ErrorMessage> messages, bool isCritical = false) =>
        new(messages, ErrorType.VALIDATION, isCritical);

    public static Error NotFound(params IEnumerable<ErrorMessage> messages) =>
        new(messages, ErrorType.NOT_FOUND);

    public static Error Failure(IEnumerable<ErrorMessage> messages, bool isCritical = false) =>
        new(messages, ErrorType.FAILURE, isCritical);

    public static Error Conflict(params IEnumerable<ErrorMessage> messages) =>
        new(messages, ErrorType.CONFLICT);

    public static Error Authentication(params IEnumerable<ErrorMessage> messages) =>
        new(messages, ErrorType.AUTHENTICATION);

    public static Error Authorization(params IEnumerable<ErrorMessage> messages) =>
        new(messages, ErrorType.AUTHORIZATION);

    public static Error RateLimited(params IEnumerable<ErrorMessage> messages) =>
        new(messages, ErrorType.RATE_LIMITED);

    public Error AsCritical() => new(Messages, Type, isCritical: true);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ErrorType
{
    VALIDATION,
    NOT_FOUND,
    FAILURE,
    CONFLICT,
    AUTHENTICATION,
    AUTHORIZATION,

    /// <summary>Клиент превысил лимит запросов — маппится в HTTP 429, не 500 (это не отказ сервера).</summary>
    /// <remarks>The client exceeded a rate limit — maps to HTTP 429, not 500 (this isn't a server failure).</remarks>
    RATE_LIMITED,

    /// <summary>Запрос синтаксически корректен, но бизнес-состояние не позволяет его выполнить —
    /// маппится в HTTP 422 (например, "товар не готов к модерации").</summary>
    /// <remarks>The request is syntactically valid but business state prevents it —
    /// maps to HTTP 422 (e.g. "product not ready for review").</remarks>
    UNPROCESSABLE_ENTITY,
}