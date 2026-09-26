namespace RewardsService.Infrastructure.Consumers;

// Local copy of AuthService's AccountProvisioned payload, same reasoning as
// EmployeeHiredEvent next to it. Consumed purely to learn the EmployeeId -> AccountId
// pairing, so that a caller's "sub" claim can be resolved to the wallet they own.
public record AccountProvisionedEvent(Guid EmployeeId, Guid AccountId)
{
    public const string MessageType = "AccountProvisioned";
}
