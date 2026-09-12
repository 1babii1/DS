namespace EmployeeService.Application.IntegrationEvents;

public static class EmployeeEventTypes
{
    public const string Hired = "EmployeeHired";
    public const string Transferred = "EmployeeTransferred";
}

public record EmployeeHiredEvent(Guid EmployeeId, string FullName, string Email, Guid DepartmentId, Guid PositionId);

public record EmployeeTransferredEvent(Guid EmployeeId, Guid DepartmentId, Guid PositionId);
