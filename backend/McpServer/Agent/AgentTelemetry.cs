using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace McpServer.Agent;

// What McpServer can honestly see of the agent: proposals made and refused, how confirmations ended, which
// steps failed. It cannot see the model - tokens and latency belong to whoever runs the model - so none of
// that is claimed here. Names match the meter and source already exported to the collector.
//
// An instance, not statics, so a test can watch its own instruments without picking up another test's.
public sealed class AgentTelemetry : IDisposable
{
    public const string Name = "mcp-server";

    public AgentTelemetry()
    {
        Meter = new Meter(Name);
        Source = new ActivitySource(Name);
        Proposals = Meter.CreateCounter<long>(
            "agent_proposals", description: "Proposals a model made, by tool and whether they were accepted or refused");
        Confirmations = Meter.CreateCounter<long>(
            "agent_confirmations", description: "Plan confirmations by how they ended; rejected ones carry a reason");
        Steps = Meter.CreateCounter<long>(
            "agent_steps", description: "Plan steps by kind and outcome");
    }

    public Meter Meter { get; }

    public ActivitySource Source { get; }

    public Counter<long> Proposals { get; }

    public Counter<long> Confirmations { get; }

    public Counter<long> Steps { get; }

    public void Proposal(string tool, bool accepted) =>
        Proposals.Add(1, new("gen_ai.tool.name", tool), new("outcome", accepted ? "proposed" : "refused"));

    public void Confirmation(string outcome, string? reason = null) =>
        Confirmations.Add(1, new("outcome", outcome), new("reason", reason ?? "none"));

    public void Step(StepKind kind, StepOutcome outcome) =>
        Steps.Add(1, new("kind", kind.ToString()), new("outcome", outcome.ToString()));

    public void Dispose()
    {
        Meter.Dispose();
        Source.Dispose();
    }
}
