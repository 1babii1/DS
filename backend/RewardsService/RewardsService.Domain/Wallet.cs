namespace RewardsService.Domain;

// A projection, not the source of truth: the wallet's events (WalletEvent, ADR 0031) are, and Balance is the fold of them,
// written in the same SaveChanges call. Reading a wallet never needs a SUM() or a replay. It can be rebuilt from the events.
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

    /// <summary>The projection of the wallet's events (ADR 0031): the balance is set from the fold, never added to.</summary>
    public void SetBalance(decimal balance)
    {
        Balance = balance;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Apply(decimal amount)
    {
        Balance += amount;
        UpdatedAt = DateTime.UtcNow;
    }
}
