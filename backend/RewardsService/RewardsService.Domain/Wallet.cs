namespace RewardsService.Domain;

// Cached balance, not the source of truth - Transaction is the append-only ledger and
// Balance is only ever moved by summing into it as each Transaction is written, in the
// same SaveChanges call. Reading a wallet never needs a SUM() over the whole ledger.
public class Wallet
{
    public Guid EmployeeId { get; private set; }

    public decimal Balance { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    private Wallet()
    {
    }

    public static Wallet Create(Guid employeeId)
    {
        var now = DateTime.UtcNow;
        return new Wallet
        {
            EmployeeId = employeeId,
            Balance = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Apply(decimal amount)
    {
        Balance += amount;
        UpdatedAt = DateTime.UtcNow;
    }
}
