namespace McpServer.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    // Base64, at least 32 bytes. Required in Production; elsewhere an ephemeral key is generated at
    // startup, which means plans do not survive a restart and are not valid across instances.
    public string? SigningKeyBase64 { get; init; }

    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(10);

    // A ceiling on what one plan may hand out, so a manipulated or mistaken request cannot propose an
    // arbitrary amount. Who may grant at all stays with RewardsService.
    public decimal MaxGrantAmount { get; init; } = 1000m;
}
