namespace EmployeeService.Domain;

public enum EmployeeStatus
{
    // Set on hire, before AuthService has confirmed a login account was
    // provisioned - see the Hire Employee saga (AuthEventsConsumer).
    PendingProvisioning = 0,
    Active = 1,
    Terminated = 2,
    ProvisioningFailed = 3,
}