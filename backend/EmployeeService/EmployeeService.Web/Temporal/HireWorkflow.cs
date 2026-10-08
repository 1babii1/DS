using EmployeeService.Domain;
using Temporalio.Common;
using Temporalio.Workflows;

namespace EmployeeService.Web.Temporal;

/// <param name="HiredAt">When the person was hired; the deadline counts from it, not from the moment the workflow was started (which, after a
/// failed start, is later). Null means "now".</param>
public sealed record HireWorkflowInput(Guid EmployeeId, TimeSpan Timeout, TimeSpan LateStepGrace, DateTime? HiredAt = null);

public sealed record CompensateInput(Guid EmployeeId, string Reason, bool RevokeAccount, bool ReverseBonus);

/// <summary>
/// The onboarding of a hire (ADR 0032) as a Temporal workflow (ADR 0056). The decisions are the saga's own: the state is the same pure
/// <see cref="HireSaga"/>, fed the same facts and the same clock, and answers with the same compensation. What Temporal replaces is everything
/// around it: the row in Postgres (the workflow's history is the state, rebuilt by replay), the worker that looks at deadlines (a durable timer
/// that fires even if no worker was alive when it came due), and the retries of the compensation (an activity with a retry policy).
///
/// A workflow's code must give the same answer every time it is replayed, so it reads the time only from <see cref="Workflow.UtcNow"/>, never
/// from the clock, and does its effects only through activities.
/// </summary>
[Workflow]
public class HireWorkflow
{
    public const string AccountProvisionedSignal = "AccountProvisioned";
    public const string BonusGrantedSignal = "BonusGranted";
    public const string AccountProvisioningFailedSignal = "AccountProvisioningFailed";

    private static readonly ActivityOptions CompensateOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),

        // No attempt limit: the undoing has to happen eventually, and a database that is down for a minute is no reason to give up on it.
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(30),
        },
    };

    private readonly Queue<Func<HireSaga, DateTime, Compensation?>> facts = new();

    [WorkflowRun]
    public async Task<string> RunAsync(HireWorkflowInput input)
    {
        var saga = HireSaga.Start(input.EmployeeId, input.HiredAt ?? Workflow.UtcNow, input.Timeout);
        DateTime? graceEnds = null;

        while (true)
        {
            // Facts first, in the order they came. Each may decide to undo the hire.
            while (facts.TryDequeue(out var fact))
            {
                var compensation = fact(saga, Workflow.UtcNow);
                if (compensation is not null)
                {
                    await Compensate(input.EmployeeId, compensation);
                }
            }

            if (saga.State == HireSagaState.Completed)
            {
                return nameof(HireSagaState.Completed);
            }

            TimeSpan wait;
            if (saga.State == HireSagaState.Started)
            {
                wait = saga.Deadline - Workflow.UtcNow;
            }
            else
            {
                // The hire has been undone. A step that finishes now was not covered by the first request; stay a while to hear of it.
                graceEnds ??= Workflow.UtcNow + input.LateStepGrace;
                wait = graceEnds.Value - Workflow.UtcNow;
                if (wait <= TimeSpan.Zero)
                {
                    return nameof(HireSagaState.CompensationRequested);
                }
            }

            var heard = await Workflow.WaitConditionAsync(() => facts.Count > 0, wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
            if (!heard && saga.State == HireSagaState.Started)
            {
                // The deadline came with nothing more to hear: time is a fact like any other.
                var compensation = saga.OnTimeCheck(Workflow.UtcNow);
                if (compensation is not null)
                {
                    await Compensate(input.EmployeeId, compensation);
                }
            }
        }
    }

    [WorkflowSignal(AccountProvisionedSignal)]
    public Task AccountProvisionedAsync()
    {
        facts.Enqueue((saga, now) => saga.OnAccountProvisioned(now));
        return Task.CompletedTask;
    }

    [WorkflowSignal(BonusGrantedSignal)]
    public Task BonusGrantedAsync()
    {
        facts.Enqueue((saga, now) => saga.OnBonusGranted(now));
        return Task.CompletedTask;
    }

    [WorkflowSignal(AccountProvisioningFailedSignal)]
    public Task AccountProvisioningFailedAsync(string reason)
    {
        facts.Enqueue((saga, now) => saga.OnAccountProvisioningFailed(reason, now));
        return Task.CompletedTask;
    }

    private static Task Compensate(Guid employeeId, Compensation compensation) =>
        Workflow.ExecuteActivityAsync(
            (HireActivities activities) => activities.CompensateAsync(
                new CompensateInput(employeeId, compensation.Reason, compensation.RevokeAccount, compensation.ReverseBonus)),
            CompensateOptions);
}
