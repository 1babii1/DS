using Microsoft.AspNetCore.Identity;

namespace AuthService.Domain;

public class Account : IdentityUser<Guid>
{
    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Set only for accounts provisioned by the Hire Employee saga
    // (EmployeeEventsConsumer) - null for accounts created through normal
    // self-registration. Doubles as the consumer's idempotency key: an
    // EmployeeHired event redelivered after the account already exists is a
    // no-op rather than a duplicate-account attempt.
    public Guid? EmployeeId { get; set; }
}