namespace NotificationService.Web.Consumers;

// Deliberately a local copy of EmployeeService's EmployeeTransferredEvent - same reasoning
// as every other cross-service event copy in this codebase. EmployeeHired/
// EmployeeTerminated also arrive on employee.events but neither has a v1 notification (see
// ADR 0006): EmployeeHired's only plausible recipient has no account yet, and
// EmployeeTerminated's account-lock semantics aren't implemented.
public record EmployeeTransferredEvent(Guid EmployeeId, Guid DepartmentId, Guid PositionId)
{
    public const string MessageType = "EmployeeTransferred";
}
