using McpServer.Api;
using Microsoft.Extensions.Logging;


namespace McpServer.Agent;

// Runs a plan the user has confirmed, as that user (the command clients forward the caller's token), one
// step at a time. The steps come from a verified signature, but each is checked again here: what runs is
// decided by this code, never by whatever happens to be inside a token.
public sealed class PlanExecutor(
    EmployeeCommandClient employees,
    RewardsCommandClient rewards,
    AgentOptions options,
    ILogger<PlanExecutor> logger,
    AgentTelemetry telemetry)
{
    public async Task<ExecutionReport> ExecuteAsync(Plan plan, CancellationToken ct)
    {
        using var planSpan = telemetry.Source.StartActivity("agent.execute");
        planSpan?.SetTag("agent.plan_id", plan.Id.ToString());
        planSpan?.SetTag("agent.steps", plan.Steps.Count);

        var results = new List<StepResult>(plan.Steps.Count);
        var createdByStep = new Dictionary<int, Guid>();
        var stopped = false;

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];

            if (stopped)
            {
                results.Add(new StepResult(i, step.Summary, StepOutcome.NotRun, "An earlier step failed.", null));
                telemetry.Step(step.Kind, StepOutcome.NotRun);
                continue;
            }

            using var stepSpan = telemetry.Source.StartActivity("agent.step");
            stepSpan?.SetTag("agent.step.index", i);
            stepSpan?.SetTag("agent.step.kind", step.Kind.ToString());

            try
            {
                var created = await RunStepAsync(plan, i, step, createdByStep, ct);
                if (created is { } id)
                {
                    createdByStep[i] = id;
                }

                results.Add(new StepResult(i, step.Summary, StepOutcome.Applied, null, created));
                telemetry.Step(step.Kind, StepOutcome.Applied);
                stepSpan?.SetTag("agent.step.outcome", "Applied");
                logger.LogInformation(
                    "Agent plan {PlanId} step {Step} ({Kind}) applied for user {UserId}", plan.Id, i, step.Kind, plan.UserId);
            }
            catch (Exception ex) when (ex is ServiceApiException or InvalidPlanStepException or HttpRequestException
                or Polly.CircuitBreaker.BrokenCircuitException or Polly.Timeout.TimeoutRejectedException)
            {
                stopped = true;
                var detail = ex is ServiceApiException or InvalidPlanStepException
                    ? ex.Message
                    : "The service could not be reached.";
                results.Add(new StepResult(i, step.Summary, StepOutcome.Failed, detail, null));
                telemetry.Step(step.Kind, StepOutcome.Failed);
                stepSpan?.SetTag("agent.step.outcome", "Failed");
                logger.LogWarning(
                    "Agent plan {PlanId} step {Step} ({Kind}) failed for user {UserId}: {Detail}",
                    plan.Id, i, step.Kind, plan.UserId, detail);
            }
        }

        return new ExecutionReport(plan.Id, !stopped, results);
    }

    private async Task<Guid?> RunStepAsync(
        Plan plan, int index, PlanStep step, Dictionary<int, Guid> createdByStep, CancellationToken ct)
    {
        switch (step.Kind)
        {
            case StepKind.HireEmployee:
            {
                var (name, email, department, position) = Require.Hire(step);
                return await employees.HireAsync(name, email, department, position, ct);
            }

            case StepKind.TransferEmployee:
            {
                var (employee, department, position) = Require.Transfer(step);
                await employees.TransferAsync(employee, department, position, ct);
                return null;
            }

            case StepKind.GrantCurrency:
            {
                var employee = ResolveEmployee(step, index, plan, createdByStep);
                var (amount, reason) = Require.Grant(step, options.MaxGrantAmount);

                // Deterministic per plan and step: confirming the same plan again sends the same key, and the
                // ledger answers with the original grant instead of making a second one.
                return await rewards.GrantAsync(employee, amount, reason, $"agent-{plan.Id:N}-{index}", ct);
            }

            default:
                throw new InvalidPlanStepException("Unknown step.");
        }
    }

    private static Guid ResolveEmployee(PlanStep step, int index, Plan plan, Dictionary<int, Guid> createdByStep)
    {
        if (step.EmployeeId is { } direct && step.EmployeeFromStep is null && direct != Guid.Empty)
        {
            return direct;
        }

        // Only a hire that came earlier in this same plan and has actually been applied can supply the id.
        if (step.EmployeeFromStep is { } from
            && step.EmployeeId is null
            && from >= 0
            && from < index
            && plan.Steps[from].Kind == StepKind.HireEmployee
            && createdByStep.TryGetValue(from, out var hired))
        {
            return hired;
        }

        throw new InvalidPlanStepException("The grant does not name a valid employee.");
    }
}

public sealed class InvalidPlanStepException(string message) : Exception(message);

internal static class Require
{
    private const int MaxText = 200;
    private const int MaxReason = 500;

    public static (string Name, string Email, Guid Department, Guid Position) Hire(PlanStep s)
    {
        if (!Text.IsClean(s.FullName, MaxText) || !Text.IsEmail(s.Email)
            || s.DepartmentId is null || s.DepartmentId == Guid.Empty
            || s.PositionId is null || s.PositionId == Guid.Empty)
        {
            throw new InvalidPlanStepException("The hire step is incomplete or invalid.");
        }

        return (s.FullName!, s.Email!, s.DepartmentId.Value, s.PositionId.Value);
    }

    public static (Guid Employee, Guid Department, Guid Position) Transfer(PlanStep s)
    {
        if (s.EmployeeId is null || s.EmployeeId == Guid.Empty
            || s.DepartmentId is null || s.DepartmentId == Guid.Empty
            || s.PositionId is null || s.PositionId == Guid.Empty)
        {
            throw new InvalidPlanStepException("The transfer step is incomplete or invalid.");
        }

        return (s.EmployeeId.Value, s.DepartmentId.Value, s.PositionId.Value);
    }

    public static (decimal Amount, string Reason) Grant(PlanStep s, decimal max)
    {
        if (s.Amount is not { } amount || amount <= 0 || amount > max)
        {
            throw new InvalidPlanStepException($"The grant amount must be more than 0 and at most {max}.");
        }

        if (!Text.IsClean(s.Reason, MaxReason))
        {
            throw new InvalidPlanStepException("The grant needs a reason.");
        }

        return (amount, s.Reason!);
    }
}

// Free text that ends up in a plan the user reads, and later in a service: single line, bounded, and free of
// anything invisible or that changes how text is displayed. char.IsControl alone is not enough: a
// right-to-left override, a zero-width space, a byte-order mark or a line separator are "format" and
// "separator" characters, not controls, and each can make a name read differently from what it is.
// Letters of any script, accents, apostrophes and emoji (valid surrogate pairs) stay allowed.
internal static class Text
{
    public static bool IsClean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            // A lone surrogate is enumerated as the replacement character: malformed text.
            if (rune == System.Text.Rune.ReplacementChar)
            {
                return false;
            }

            switch (System.Text.Rune.GetUnicodeCategory(rune))
            {
                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:
                case System.Globalization.UnicodeCategory.PrivateUse:
                case System.Globalization.UnicodeCategory.OtherNotAssigned:
                    return false;
            }
        }

        return true;
    }

    public static bool IsEmail(string? value) =>
        IsClean(value, 254)
        && System.Net.Mail.MailAddress.TryCreate(value, out var address)
        && address.Address == value;
}
