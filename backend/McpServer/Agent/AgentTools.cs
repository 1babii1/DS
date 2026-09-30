using System.ComponentModel;
using System.Diagnostics;
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
//
// Every id the model passes is resolved against the owning services first (PlanLookup, reads as the caller), and
// the plan is written from what they answer, in names: an id that points at nothing, or at the wrong kind of thing,
// never becomes a plan, and the person approving reads who and what, not GUIDs.
[McpServerToolType]
public sealed class AgentTools(
    IHttpContextAccessor httpContextAccessor,
    PlanSigner signer,
    IOptions<AgentOptions> options,
    TimeProvider clock,
    AgentTelemetry telemetry,
    PlanLookup lookup)
{
    private const string Next =
        "Nothing has been done yet. Show the user these steps and ask them to approve; only their own approval " +
        "(POST /mcp/plans/confirm with this plan token) runs them.";

    [McpServerTool(Name = "propose_hire_employee")]
    [Description("Proposes hiring an employee. Changes nothing: returns a plan for the user to approve. Do not tell the user the hire has happened. A hire carries no currency grant; that is a separate proposal.")]
    public Task<PlanProposal> ProposeHire(
        [Description("Full name")] string fullName,
        [Description("Email address")] string email,
        [Description("Department id")] Guid departmentId,
        [Description("Position id: a position that department has")] Guid positionId,
        CancellationToken cancellationToken = default) =>
        Guarded("propose_hire_employee", async () =>
    {
        RequireUser();
        RequireIds(departmentId, positionId);
        var department = await lookup.DepartmentNameAsync(departmentId, cancellationToken);
        var position = await lookup.PositionNameAsync(departmentId, positionId, cancellationToken);

        return [new PlanStep(
            StepKind.HireEmployee,
            $"Hire \"{fullName}\" <{email}> into \"{department}\" as \"{position}\"",
            FullName: fullName,
            Email: email,
            DepartmentId: departmentId,
            PositionId: positionId,
            DepartmentName: department,
            PositionName: position)];
    });

    [McpServerTool(Name = "propose_transfer_employee")]
    [Description("Proposes moving an employee to another department and position. Changes nothing: returns a plan for the user to approve.")]
    public Task<PlanProposal> ProposeTransfer(
        [Description("Employee id")] Guid employeeId,
        [Description("Target department id")] Guid departmentId,
        [Description("Target position id: a position the target department has")] Guid positionId,
        CancellationToken cancellationToken = default) =>
        Guarded("propose_transfer_employee", async () =>
    {
        RequireUser();
        RequireIds(employeeId, departmentId, positionId);
        var employee = await lookup.EmployeeAsync(employeeId, cancellationToken);
        var department = await lookup.DepartmentNameAsync(departmentId, cancellationToken);
        var position = await lookup.PositionNameAsync(departmentId, positionId, cancellationToken);

        return [new PlanStep(
            StepKind.TransferEmployee,
            $"Move \"{employee.FullName}\" (now \"{employee.DepartmentName}\", \"{employee.PositionName}\") to \"{department}\" as \"{position}\"",
            EmployeeId: employeeId,
            DepartmentId: departmentId,
            PositionId: positionId,
            EmployeeName: employee.FullName,
            DepartmentName: department,
            PositionName: position)];
    });

    [McpServerTool(Name = "propose_grant_currency")]
    [Description("Proposes granting internal currency to an existing employee. Changes nothing: returns a plan for the user to approve. Large grants are refused.")]
    public Task<PlanProposal> ProposeGrant(
        [Description("Employee id")] Guid employeeId,
        [Description("Amount, more than 0")] decimal amount,
        [Description("Reason for the grant")] string reason,
        CancellationToken cancellationToken = default) =>
        Guarded("propose_grant_currency", async () =>
    {
        RequireUser();
        RequireIds(employeeId);
        var employee = await lookup.EmployeeAsync(employeeId, cancellationToken);

        return [new PlanStep(
            StepKind.GrantCurrency,
            $"Grant {amount} to \"{employee.FullName}\" (\"{employee.DepartmentName}\", \"{employee.PositionName}\"): \"{reason}\"",
            EmployeeId: employeeId,
            Amount: amount,
            Reason: reason,
            EmployeeName: employee.FullName,
            DepartmentName: employee.DepartmentName,
            PositionName: employee.PositionName)];
    });

    // Before any lookup: without a signed-in user there is no token to read with and nobody to bind the plan to.
    private Guid RequireUser() =>
        Guid.TryParse(httpContextAccessor.HttpContext?.User.FindFirstValue("sub"), out var userId)
            ? userId
            : throw new McpException("Your session is not valid for this service.");

    private static void RequireIds(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
        {
            throw new McpException("An id is missing. Use the ids you found by looking things up.");
        }
    }

    // One span and one count per proposal, over everything that can refuse it: the session, the ids, the lookups and
    // the plan's own checks. A refusal at any of those is a refusal in the numbers.
    private async Task<PlanProposal> Guarded(string tool, Func<Task<List<PlanStep>>> resolve)
    {
        using var span = telemetry.Source.StartActivity("agent.propose");
        span?.SetTag("gen_ai.tool.name", tool);

        try
        {
            var steps = await resolve();
            span?.SetTag("agent.steps", steps.Count);
            var proposal = Build(steps);
            telemetry.Proposal(tool, accepted: true);
            return proposal;
        }
        catch (McpException)
        {
            telemetry.Proposal(tool, accepted: false);
            span?.SetTag("agent.outcome", "refused");
            throw;
        }
    }

    private PlanProposal Build(List<PlanStep> steps)
    {
        var userId = RequireUser();

        var now = clock.GetUtcNow();
        var plan = new Plan(Guid.NewGuid(), userId, now, now + options.Value.PlanLifetime, steps);

        // The same checks the executor applies, so a plan that could never run is refused here, at the
        // moment the model proposes it, rather than after the user has approved it.
        for (var i = 0; i < steps.Count; i++)
        {
            Validate(steps[i], i, options.Value);
        }

        Activity.Current?.SetTag("agent.plan_id", plan.Id.ToString());
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
