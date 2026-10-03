namespace EmployeeService.Domain;

/// <summary>
/// What this service knows about an employee's wallet, learned from RewardsService's events and kept only to answer reads
/// (the employee card, ADR 0034). Not authoritative: the wallet belongs to RewardsService, this is a copy that trails it by the
/// time the event takes to arrive. <see cref="WalletVersion"/> is the version of the wallet's own history that the balance
/// came from, which is both the guard against old events overwriting new ones and what a reader waits on.
/// </summary>
public class EmployeeWallet
{
    public Guid EmployeeId { get; private set; }

    public decimal Balance { get; private set; }

    public int WalletVersion { get; private set; }

    /// <summary>When the balance changed at the source (the event's own time), for measuring how far behind this copy is.</summary>
    public DateTime BalanceChangedAt { get; private set; }

    /// <summary>When this copy took the change.</summary>
    public DateTime ProjectedAt { get; private set; }
}
