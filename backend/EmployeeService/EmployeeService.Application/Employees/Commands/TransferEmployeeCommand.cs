namespace EmployeeService.Application.Employees.Commands;

public record TransferEmployeeCommand(
    Guid EmployeeId,
    Guid DepartmentId,
    Guid PositionId,
    Guid? CallerAccountId = null,
    bool CallerIsAdmin = false);