namespace EmployeeService.Web.Consumers;

// EmployeeService's own copy of what it reads of RewardsService's CurrencyGranted: whose wallet, and why (the source says
// whether it was the welcome bonus the onboarding process is waiting for).
public record CurrencyGrantedEvent(Guid EmployeeId, string? Source = null)
{
    public const string MessageType = "CurrencyGranted";

    public const string WelcomeBonusSource = "WelcomeBonus";
}
