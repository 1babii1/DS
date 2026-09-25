namespace McpServer.Agent;

public enum StepKind
{
    HireEmployee,
    TransferEmployee,
    GrantCurrency,
}

// One thing the plan will do. Flat on purpose (a Kind plus optional fields, not a class per step):
// what gets signed and later executed is plain data with nothing to deserialize into an unexpected type.
public sealed record PlanStep(
    StepKind Kind,
    string Summary,
    string? FullName = null,
    string? Email = null,
    Guid? DepartmentId = null,
    Guid? PositionId = null,
    Guid? EmployeeId = null,
    int? EmployeeFromStep = null,
    decimal? Amount = null,
    string? Reason = null);

// What the user is asked to approve, and exactly what will run. Bound to one user and to a moment:
// the signature covers all of it, so none of it can be changed after the user has seen it.
public sealed record Plan(
    Guid Id,
    Guid UserId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<PlanStep> Steps);

public enum StepOutcome
{
    Applied,
    Failed,
    NotRun,
}

public sealed record StepResult(int Index, string Summary, StepOutcome Outcome, string? Detail, Guid? CreatedId);

// What actually happened, step by step. If a step fails the later ones are NotRun and say so: the report
// must be true, because compensation for applied steps is not attempted.
public sealed record ExecutionReport(Guid PlanId, bool Completed, IReadOnlyList<StepResult> Steps);
