using Shared;

namespace EmployeeService.Application.Employees.Errors;

public static class EmployeeErrors
{
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

    public static Error DirectoryUnavailable() =>
        Error.Failure("employee.directory.unavailable", "DirectoryService is temporarily unavailable");

    public static Error NotFound(Guid employeeId) =>
        Error.NotFound("employee.not_found", $"Employee '{employeeId}' does not exist", "employeeId");
}
