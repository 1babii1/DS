namespace McpServer.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    // Base64, at least 32 bytes. Required in Production; elsewhere an ephemeral key is generated at
    // startup, which means plans do not survive a restart and are not valid across instances.
    public string? SigningKeyBase64 { get; init; }

    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(10);

    // An early, readable refusal for a grant the ledger would refuse anyway: RewardsService holds the same limit
    // for agent grants (500) and is the one that enforces it, together with the daily quota.
    public decimal MaxGrantAmount { get; init; } = 500m;
}
