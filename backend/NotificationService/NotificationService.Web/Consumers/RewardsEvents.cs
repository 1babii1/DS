namespace NotificationService.Web.Consumers;

// Deliberately a local copy of RewardsService's CurrencyGrantedEvent, not a shared
// type/ProjectReference - same reasoning as every other cross-service event copy in this
// codebase. Only the fields this consumer actually needs.
public record CurrencyGrantedEvent(Guid EmployeeId, decimal Amount, string Reason, decimal NewBalance)
{
    public const string MessageType = "CurrencyGranted";
}
