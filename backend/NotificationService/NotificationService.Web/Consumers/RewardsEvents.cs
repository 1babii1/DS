namespace NotificationService.Web.Consumers;

// Deliberately a local copy of RewardsService's CurrencyGrantedEvent, not a shared
// type/ProjectReference - same reasoning as every other cross-service event copy in this
// codebase. Only the fields this consumer actually needs.
public record CurrencyGrantedEvent(Guid EmployeeId, decimal Amount, string Reason, decimal NewBalance, string? Source = null)
{
    public const string MessageType = "CurrencyGranted";

    // The undo of a welcome bonus the person never saw (ADR 0032): the balance changed, there is nothing to announce.
    public const string WelcomeBonusReversalSource = "WelcomeBonusReversal";
}
