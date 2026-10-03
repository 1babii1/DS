namespace AuthService.Web.Consumers;

// AuthService's own copy of what it reads of EmployeeService's HireCompensationRequested (ADR 0032): who, and whether the
// account is among the steps to undo.
public record HireCompensationRequestedEvent(Guid EmployeeId, string Reason, bool RevokeAccount)
{
    public const string MessageType = "HireCompensationRequested";
}
