namespace EmployeeService.Web.Consumers;

// EmployeeService's own copy of what it reads of RewardsService's CurrencyGranted: whose wallet, why (the welcome bonus the
// onboarding process waits for), and where the wallet stands (the balance and the version of its history, which the employee
// card keeps, ADR 0034).
public record CurrencyGrantedEvent(Guid EmployeeId, decimal NewBalance, string? Source = null, int? WalletVersion = null)
{
    public const string MessageType = "CurrencyGranted";

    public const string WelcomeBonusSource = "WelcomeBonus";
}
