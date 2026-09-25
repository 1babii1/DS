using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.Agent;

/// <summary>What the model hands back to the user: a plan to read, and a token to approve it with.</summary>
public sealed record PlanProposal(string PlanToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Steps, string Next);

// The model can only PROPOSE. Nothing here changes any data, and there is deliberately no tool that confirms
// or runs a plan: confirmation is a separate request the user makes with their own session
// (POST /mcp/plans/confirm), which a model has no tool to call. A test holds the tool list to that.
[McpServerToolType]
public sealed class AgentTools(
    IHttpContextAccessor httpContextAccessor,
    PlanSigner signer,
    IOptions<AgentOptions> options,
    TimeProvider clock)
{
    private const string Next =
        "Nothing has been done yet. Show the user these steps and ask them to approve; only their own approval " +
        "(POST /mcp/plans/confirm with this plan token) runs them.";

    [McpServerTool(Name = "propose_hire_employee")]
    [Description("Proposes hiring an employee, optionally followed by a one-off currency grant to them. Changes nothing: returns a plan for the user to approve. Do not tell the user the hire has happened.")]
    public PlanProposal ProposeHire(
        [Description("Full name")] string fullName,
        [Description("Email address")] string email,
        [Description("Department id")] Guid departmentId,
        [Description("Position id")] Guid positionId,
        [Description("Optional currency to grant the new employee on top of the automatic welcome bonus")] decimal? grantAmount = null,
        [Description("Reason for that grant; required if grantAmount is given")] string? grantReason = null)
    {
        var steps = new List<PlanStep>
        {
            new(
                StepKind.HireEmployee,
                $"Hire \"{fullName}\" <{email}> into department {departmentId} as position {positionId}",
                FullName: fullName,
                Email: email,
                DepartmentId: departmentId,
                PositionId: positionId),
        };

        if (grantAmount is { } amount)
        {
            steps.Add(new PlanStep(
                StepKind.GrantCurrency,
                $"Grant {amount} to the new employee: \"{grantReason}\"",
                EmployeeFromStep: 0,
                Amount: amount,
                Reason: grantReason));
        }

        return Propose(steps);
    }

    [McpServerTool(Name = "propose_transfer_employee")]
    [Description("Proposes moving an employee to another department and position. Changes nothing: returns a plan for the user to approve.")]
    public PlanProposal ProposeTransfer(
        [Description("Employee id")] Guid employeeId,
        [Description("Target department id")] Guid departmentId,
        [Description("Target position id")] Guid positionId) =>
        Propose([new PlanStep(
            StepKind.TransferEmployee,
            $"Transfer employee {employeeId} to department {departmentId}, position {positionId}",
            EmployeeId: employeeId,
            DepartmentId: departmentId,
            PositionId: positionId)]);

    [McpServerTool(Name = "propose_grant_currency")]
    [Description("Proposes granting internal currency to an existing employee. Changes nothing: returns a plan for the user to approve.")]
    public PlanProposal ProposeGrant(
        [Description("Employee id")] Guid employeeId,
        [Description("Amount, more than 0")] decimal amount,
        [Description("Reason for the grant")] string reason) =>
        Propose([new PlanStep(
            StepKind.GrantCurrency,
            $"Grant {amount} to employee {employeeId}: \"{reason}\"",
            EmployeeId: employeeId,
            Amount: amount,
            Reason: reason)]);

    private PlanProposal Propose(List<PlanStep> steps)
    {
        var user = httpContextAccessor.HttpContext?.User.FindFirstValue("sub");
        if (!Guid.TryParse(user, out var userId))
        {
            throw new McpException("Your session is not valid for this service.");
        }

        var now = clock.GetUtcNow();
        var plan = new Plan(Guid.NewGuid(), userId, now, now + options.Value.PlanLifetime, steps);

        // The same checks the executor applies, so a plan that could never run is refused here, at the
        // moment the model proposes it, rather than after the user has approved it.
        for (var i = 0; i < steps.Count; i++)
        {
            Validate(steps[i], i, options.Value);
        }

        return new PlanProposal(signer.Sign(plan), plan.ExpiresAt, steps.Select(s => s.Summary).ToList(), Next);
    }

    private static void Validate(PlanStep step, int index, AgentOptions options)
    {
        try
        {
            switch (step.Kind)
            {
                case StepKind.HireEmployee:
                    Require.Hire(step);
                    break;
                case StepKind.TransferEmployee:
                    Require.Transfer(step);
                    break;
                case StepKind.GrantCurrency:
                    Require.Grant(step, options.MaxGrantAmount);
                    if (step.EmployeeFromStep is null && (step.EmployeeId is null || step.EmployeeId == Guid.Empty))
                    {
                        throw new InvalidPlanStepException("The grant does not name a valid employee.");
                    }

                    break;
            }
        }
        catch (InvalidPlanStepException ex)
        {
            throw new McpException($"Step {index + 1}: {ex.Message}", ex);
        }
    }
}
