namespace EmployeeService.Application.Employees.Commands;

public record HireEmployeeCommand(string FullName, string Email, Guid DepartmentId, Guid PositionId);