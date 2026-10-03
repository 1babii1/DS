namespace EmployeeService.Application.IntegrationEvents;

public static class EmployeeEventTypes
{
    public const string Hired = "EmployeeHired";
    public const string Transferred = "EmployeeTransferred";
    public const string Terminated = "EmployeeTerminated";
    public const string HireCompensationRequested = "HireCompensationRequested";
}

public record EmployeeHiredEvent(
    Guid EmployeeId,
    string FullName,
    string Email,
    Guid DepartmentId,
    Guid PositionId,
    Guid? HiredByAccountId = null);

public record EmployeeTransferredEvent(Guid EmployeeId, Guid DepartmentId, Guid PositionId);

public record EmployeeTerminatedEvent(Guid EmployeeId);
/// <summary>
/// The onboarding of a hire did not complete, and the steps that did happen are to be undone (ADR 0032): the login account
/// revoked, the welcome bonus reversed. Which ones is stated, so a participant never has to guess. It can be sent again for a
/// step that finished after the first request; receivers are idempotent.
/// </summary>
public record HireCompensationRequestedEvent(Guid EmployeeId, string Reason, bool RevokeAccount, bool ReverseBonus);
