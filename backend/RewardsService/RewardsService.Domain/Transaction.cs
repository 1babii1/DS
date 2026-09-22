namespace RewardsService.Domain;

public enum TransactionSource
{
    ManualGrant,
    WelcomeBonus,
}

// Append-only ledger: rows are never updated or deleted, unlike Wallet.Balance which is
// just a cached sum of these. Amount can go negative in the future (a currency shop
// spending balance down) - nothing here assumes it's always a credit.
public class Transaction
{
    public Guid Id { get; private set; }

    public Guid EmployeeId { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = null!;

    public TransactionSource Source { get; private set; }

    // Null for automatic grants (e.g. WelcomeBonus) - there is no human actor to record.
    public Guid? GrantedByAccountId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private Transaction()
    {
    }

    public static Transaction Create(
        Guid employeeId,
        decimal amount,
        string reason,
        TransactionSource source,
        Guid? grantedByAccountId) => new()
        {
            Id = Guid.CreateVersion7(),
            EmployeeId = employeeId,
            Amount = amount,
            Reason = reason,
            Source = source,
            GrantedByAccountId = grantedByAccountId,
            CreatedAt = DateTime.UtcNow,
        };
}
