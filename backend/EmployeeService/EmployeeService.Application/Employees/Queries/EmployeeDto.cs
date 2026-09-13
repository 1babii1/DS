namespace EmployeeService.Application.Employees.Queries;

public record EmployeeDto(
    Guid Id,
    string FullName,
    string Email,
    Guid DepartmentId,
    string DepartmentName,
    Guid PositionId,
    string PositionName,
    string Status,
    DateTime HiredAt);