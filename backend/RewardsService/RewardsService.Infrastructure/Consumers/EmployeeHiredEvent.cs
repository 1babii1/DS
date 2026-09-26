namespace RewardsService.Infrastructure.Consumers;

// Deliberately a local copy of EmployeeService's EmployeeHiredEvent, not a shared
// type/ProjectReference - this consumer owns its own expectation of the payload
// shape, same as any other Kafka consumer that doesn't share a compile-time
// contract with its producer. Only the field the welcome bonus actually needs.
public record EmployeeHiredEvent(Guid EmployeeId)
{
    public const string MessageType = "EmployeeHired";
}
