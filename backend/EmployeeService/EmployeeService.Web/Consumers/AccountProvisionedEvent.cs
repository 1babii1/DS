namespace EmployeeService.Web.Consumers;

// Local copy of AuthService's AccountProvisionedEvent - same reasoning as
// AuthService's own local EmployeeHiredEvent: this consumer owns its own
// expectation of the payload shape rather than sharing a compile-time contract
// with the producer.
public record AccountProvisionedEvent(Guid EmployeeId, Guid AccountId)
{
    public const string MessageType = "AccountProvisioned";
}
