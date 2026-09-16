namespace AuthService.Application.IntegrationEvents;

public static class AuthEventTypes
{
    public const string AccountProvisioned = "AccountProvisioned";
    public const string AccountProvisioningFailed = "AccountProvisioningFailed";
}

public record AccountProvisionedEvent(Guid EmployeeId, Guid AccountId);

public record AccountProvisioningFailedEvent(Guid EmployeeId, string Reason, Guid? HiredByAccountId = null);
