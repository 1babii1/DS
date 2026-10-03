namespace RewardsService.Infrastructure.Consumers;

// RewardsService's own copy of what it reads of EmployeeService's HireCompensationRequested (ADR 0032): who, and whether the
// welcome bonus is among the steps to undo.
public record HireCompensationRequestedEvent(Guid EmployeeId, bool ReverseBonus)
{
    public const string MessageType = "HireCompensationRequested";
}
