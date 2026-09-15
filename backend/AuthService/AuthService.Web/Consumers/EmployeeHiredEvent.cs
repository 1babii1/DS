namespace AuthService.Web.Consumers;

// Deliberately a local copy of EmployeeService's EmployeeHiredEvent, not a shared
// type/ProjectReference - this consumer owns its own expectation of the payload
// shape, same as any other Kafka consumer that doesn't share a compile-time
// contract with its producer. Only the fields this consumer actually needs.
public record EmployeeHiredEvent(Guid EmployeeId, string FullName, string Email)
{
    public const string MessageType = "EmployeeHired";
}
