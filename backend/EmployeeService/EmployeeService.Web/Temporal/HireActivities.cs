using EmployeeService.Application.Employees;
using EmployeeService.Domain;
using Temporalio.Activities;

namespace EmployeeService.Web.Temporal;

/// <summary>
/// The one effect the workflow has: undoing a hire. It is the saga's own compensation (<see cref="HireSagaCoordinator.ApplyCompensation"/>),
/// so the two orchestrators cannot drift apart on what undoing means. Temporal retries it until it succeeds, and may run it twice if an
/// acknowledgement is lost; the request it publishes is taken idempotently by the participants (ADR 0032).
/// </summary>
public class HireActivities(HireSagaCoordinator coordinator)
{
    [Activity]
    public Task CompensateAsync(CompensateInput input) =>
        coordinator.ApplyCompensation(
            input.EmployeeId,
            new Compensation(input.Reason, input.RevokeAccount, input.ReverseBonus),
            ActivityExecutionContext.Current.CancellationToken);
}
