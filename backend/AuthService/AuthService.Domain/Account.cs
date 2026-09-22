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

    // GitHub-style "sudo mode": set for a short window after the user re-verifies via
    // a fresh 2FA/email code (StepUpService), independent of how long ago they last
    // logged in. Read into the elevated_until access-token claim on the next token
    // refresh - the actual authorization check (Shared's StepUp policy) compares that
    // claim's value against the current time, not this column, since resource services
    // never call back to AuthService to re-read it live.
    public DateTime? ElevatedUntil { get; set; }

    // The IP address/time of this account's most recent successful login, kept only to
    // detect "sign-in from a new IP" for SecurityAuditService's notification email - a
    // deliberately simple heuristic (last-known IP, not a full device history), so someone
    // who alternates between two regular networks (home/office) will see an email each time
    // they switch. A real device-recognition system is a larger feature than one email.
    public string? LastLoginIp { get; set; }

    public DateTime? LastLoginAt { get; set; }
}