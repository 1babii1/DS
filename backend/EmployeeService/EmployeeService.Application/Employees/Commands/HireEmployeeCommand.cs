namespace EmployeeService.Application.Employees.Commands;

// HiredByAccountId is never trusted from the request body - the controller
// always overwrites it with the caller's own "sub" claim before this reaches
// the handler. Optional/trailing only so existing call sites (tests, mainly)
// that predate actor tracking don't all need updating for a value they don't
// care about.
public record HireEmployeeCommand(
    string FullName,
    string Email,
    Guid DepartmentId,
    Guid PositionId,
    Guid? HiredByAccountId = null);