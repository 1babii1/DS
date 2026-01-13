using Shared;

namespace DirectoryService.Application.Location.Errors;

public class LocationErrors
{
    public static Error NotFound(Guid id) => Error.NotFound("location.not_found", "Location not found", "id");

    public static Error Validation(string? code, string? message, string? invalidField = null) =>
        Error.Validation(code ?? "location.invalid", message ?? "Location is invalid", invalidField);
}