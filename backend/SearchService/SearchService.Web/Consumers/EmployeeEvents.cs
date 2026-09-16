namespace SearchService.Web.Consumers;

// Deliberately local copies of EmployeeService's integration events - same reasoning as
// every other cross-service event copy in this codebase.
public record EmployeeHiredEvent(Guid EmployeeId, string FullName, string Email, Guid DepartmentId, Guid PositionId)
{
    public const string MessageType = "EmployeeHired";
}

public record EmployeeTransferredEvent(Guid EmployeeId, Guid DepartmentId, Guid PositionId)
{
    public const string MessageType = "EmployeeTransferred";
}

public record EmployeeTerminatedEvent(Guid EmployeeId)
{
    public const string MessageType = "EmployeeTerminated";
}
