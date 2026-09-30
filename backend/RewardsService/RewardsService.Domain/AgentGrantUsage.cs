namespace RewardsService.Domain;

// How much an account has granted through the agent route on one UTC day. One row per (account, day), moved only
// by a conditional upsert in the same transaction as the ledger write, so the daily limit cannot be passed by two
// requests that each looked at the total before either wrote.
public class AgentGrantUsage
{
    public Guid GrantedByAccountId { get; private set; }

    public DateOnly Day { get; private set; }

    public decimal Used { get; private set; }

    private AgentGrantUsage()
    {
    }

    public static AgentGrantUsage Create(Guid grantedByAccountId, DateOnly day, decimal used) =>
        new() { GrantedByAccountId = grantedByAccountId, Day = day, Used = used };
}
