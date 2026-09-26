namespace NotificationService.Domain;

// A local projection of EmployeeId -> AccountId, materialized from AccountProvisioned as
// it's consumed - not a synchronous call back to AuthService. This service already
// subscribes to auth.events to notice provisioning outcomes, so it already sees every
// pair that will ever exist; the same denormalization principle as Employee.DepartmentName
// (EmployeeService), just fed by a Kafka event instead of a gRPC response.
public class AccountLookup
{
    public Guid EmployeeId { get; private set; }

    public Guid AccountId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private AccountLookup()
    {
    }

    public static AccountLookup Create(Guid employeeId, Guid accountId) => new()
    {
        EmployeeId = employeeId,
        AccountId = accountId,
        CreatedAt = DateTime.UtcNow,
    };
}
