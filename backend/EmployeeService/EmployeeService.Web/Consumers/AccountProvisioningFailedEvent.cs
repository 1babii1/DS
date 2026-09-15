namespace EmployeeService.Web.Consumers;

// Local copy of AuthService's AccountProvisioningFailedEvent - see
// AccountProvisionedEvent for why this isn't a shared type.
public record AccountProvisioningFailedEvent(Guid EmployeeId, string Reason)
{
    public const string MessageType = "AccountProvisioningFailed";
}
