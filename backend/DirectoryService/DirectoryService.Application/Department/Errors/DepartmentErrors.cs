using Shared;

namespace DirectoryService.Application.Department.Errors;

public static class DepartmentErrors
{
    public static Error NotFound(Guid id) => Error.NotFound("department.not_found", "Department not found", "id");

    public static Error Validation(string? code, string? message, string? invalidField = null) =>
        Error.Validation(code ?? "department.validation", message ?? "Department is invalid", invalidField);

    public static Error LocationsIdsNotFound() =>
        Error.NotFound("department.locations_ids_not_found", "Locations ids not found");
}