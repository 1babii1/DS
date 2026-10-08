using Shared;

namespace EmployeeService.Application.Employees.Errors;

public static class EmployeeErrors
{
    /// <summary>The code of the internal signal that the unique (Scope, Key) of an idempotency record was lost to a concurrent request.</summary>
    public const string IdempotencyRaceCode = "employee.idempotency_key.race";

    public static Error IdempotencyRace() =>
        Error.Conflict(IdempotencyRaceCode, "Another request with the same Idempotency-Key is in progress");

    public static Error DepartmentNotFound() =>
        Error.NotFound("employee.department.not_found", "Department does not exist", "departmentId");

    public static Error DepartmentInactive() =>
        Error.Validation("employee.department.inactive", "Department is not active", "departmentId");

    public static Error PositionNotFound() =>
        Error.NotFound("employee.position.not_found", "Position does not exist", "positionId");

    public static Error PositionInactive() =>
        Error.Validation("employee.position.inactive", "Position is not active", "positionId");

    public static Error PositionNotInDepartment() =>
        Error.Validation(
            "employee.position.not_in_department",
            "Position does not belong to the given department",
            "positionId");

    public static Error NotDepartmentManager() =>
        Error.Authorization("employee.department.not_manager", "You do not manage this department or any department above it", "departmentId");

    public static Error AuthorizationUnavailable() =>
        Error.Unavailable("employee.authorization.unavailable", "Authorization is temporarily unavailable; nothing was changed");

    public static Error DirectoryUnavailable() =>
        Error.Unavailable("employee.directory.unavailable", "DirectoryService is temporarily unavailable");

    public static Error DirectoryUnauthorized() =>
        Error.Authorization("employee.directory.unauthorized", "DirectoryService rejected the request credentials");

    public static Error NotFound(Guid employeeId) =>
        Error.NotFound("employee.not_found", $"Employee '{employeeId}' does not exist", "employeeId");

    public static Error ConcurrencyConflict() =>
        Error.Conflict(
            "employee.concurrency_conflict",
            "This employee was modified by another request - reload and try again");

    public static Error IdempotencyKeyReused() =>
        Error.Conflict(
            "employee.idempotency_key.reused",
            "This Idempotency-Key was already used for a different request");

    public static Error IdempotencyKeyInvalid() =>
        Error.Validation("employee.idempotency_key.invalid", "Idempotency-Key must be 1 to 200 characters", "idempotencyKey");

    public static Error EmailAlreadyExists(string email) =>
        Error.Conflict("employee.email.already_exists", $"An employee with email '{email}' already exists", "email");
}