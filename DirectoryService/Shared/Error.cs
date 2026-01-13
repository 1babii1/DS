using System.Text.Json.Serialization;

namespace Shared;

public record ErrorMessages(string code, string message, string? invalidField = null);

public class Error
{
    public IReadOnlyList<ErrorMessages> Messages { get; set; } = [];

    public ErrorType Type { get; set; }

    public Error() { }

    private Error(IEnumerable<ErrorMessages> messages, ErrorType type)
    {
        Messages = messages.ToArray();
        Type = type;
    }

    public static Error Validation(string code, string message, string? invalidField = null) =>
        new([new ErrorMessages(code, message, invalidField)], ErrorType.VALIDATION);

    public static Error NotFound(string code, string message, string? invalidField = null) =>
        new([new ErrorMessages(code, message, invalidField)], ErrorType.NOT_FOUND);

    public static Error Conflict(string code, string message, string? invalidField = null) =>
        new([new ErrorMessages(code, message, invalidField)], ErrorType.CONFLICT);

    public static Error Failure(string code, string message, string? invalidField = null) =>
        new([new ErrorMessages(code, message, invalidField)], ErrorType.FAILURE);

    public static Error None() => new([], ErrorType.NONE);

    public static Error Validation(params IEnumerable<ErrorMessages> messages) =>
        new(messages, ErrorType.VALIDATION);

    public static Error NotFound(params IEnumerable<ErrorMessages> messages) =>
        new(messages, ErrorType.NOT_FOUND);

    public static Error Conflict(params IEnumerable<ErrorMessages> messages) =>
        new(messages, ErrorType.CONFLICT);

    public static Error Failure(params IEnumerable<ErrorMessages> messages) =>
        new(messages, ErrorType.FAILURE);

    // Хотим из Error сделать Errors
    public Errors ToErrors() => this;
}