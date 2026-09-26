namespace RewardsService.Domain;

// A local projection of EmployeeId -> AccountId, materialized from AccountProvisioned as
// it's consumed. Wallets are keyed by EmployeeId, but a JWT only ever carries the caller's
// AccountId in "sub" - the two are different identifiers (AuthService creates an Account
// whose own Id is unrelated to the EmployeeId it was provisioned for). Without this
// projection there is no way to answer "which wallet belongs to the caller", which is
// exactly the bug this table fixes. Same denormalization principle, and the same shape, as
// NotificationService.AccountLookup - fed by the same auth.events stream.
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
