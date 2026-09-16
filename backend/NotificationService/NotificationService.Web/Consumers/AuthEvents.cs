namespace NotificationService.Web.Consumers;

// Deliberately local copies of AuthService's AccountProvisionedEvent/
// AccountProvisioningFailedEvent - same reasoning as every other cross-service event copy
// in this codebase.
public record AccountProvisionedEvent(Guid EmployeeId, Guid AccountId)
{
    public const string MessageType = "AccountProvisioned";
}

public record AccountProvisioningFailedEvent(Guid EmployeeId, string Reason, Guid? HiredByAccountId = null)
{
    public const string MessageType = "AccountProvisioningFailed";
}
