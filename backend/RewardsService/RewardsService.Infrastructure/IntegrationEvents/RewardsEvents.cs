namespace RewardsService.Infrastructure.IntegrationEvents;

public static class RewardsEventTypes
{
    public const string CurrencyGranted = "CurrencyGranted";
}

public record CurrencyGrantedEvent(Guid EmployeeId, decimal Amount, string Reason, decimal NewBalance);
