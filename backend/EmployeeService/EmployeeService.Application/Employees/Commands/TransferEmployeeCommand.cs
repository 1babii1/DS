namespace EmployeeService.Application.Employees.Commands;

public record TransferEmployeeCommand(Guid EmployeeId, Guid DepartmentId, Guid PositionId);