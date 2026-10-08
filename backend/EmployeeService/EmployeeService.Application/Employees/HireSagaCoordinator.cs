using EmployeeService.Application.Database;
using EmployeeService.Application.IntegrationEvents;
using EmployeeService.Domain;

namespace EmployeeService.Application.Employees;

/// <summary>
/// Carries the onboarding process (<see cref="HireSaga"/>) between the facts it hears about and the effects it causes. Each
/// method is one step and one transaction: the saga's new state, the employee's status when it is undone and the compensation
/// event in the outbox all commit together or not at all. Every step is idempotent: at-least-once delivery repeats facts.
/// </summary>
public class HireSagaCoordinator(
    IHireSagaRepository sagas,
    IEmployeeRepository employees,
    IOutboxWriter outbox,
    TimeProvider clock)
{
    public const int DeadlineBatch = 50;

    public Task OnAccountProvisioned(Guid employeeId, CancellationToken ct) =>
        Step(employeeId, (saga, now) => saga.OnAccountProvisioned(now), ct);

    public Task OnBonusGranted(Guid employeeId, CancellationToken ct) =>
        Step(employeeId, (saga, now) => saga.OnBonusGranted(now), ct);

    public Task OnAccountProvisioningFailed(Guid employeeId, string reason, CancellationToken ct) =>
        Step(employeeId, (saga, now) => saga.OnAccountProvisioningFailed(reason, now), ct);

    /// <summary>Acts on every process whose deadline has passed. Returns how many it undid.</summary>
    public async Task<int> CheckDeadlines(CancellationToken ct)
    {
        var due = await sagas.DueEmployees(clock.GetUtcNow().UtcDateTime, DeadlineBatch, ct);
        foreach (var employeeId in due)
        {
            await Step(employeeId, (saga, now) => saga.OnTimeCheck(now), ct);
        }

        return due.Count;
    }

    /// <summary>
    /// What undoing a hire is: the employee's status and the request to the other services, in one transaction. Used by the saga's own steps
    /// and by the Temporal workflow's activity (ADR 0056), so that the two carry out exactly the same compensation. Idempotent in effect; a
    /// repeat after a lost acknowledgement publishes the request again, which the participants take idempotently (ADR 0032).
    /// </summary>
    public async Task ApplyCompensation(Guid employeeId, Compensation compensation, CancellationToken ct)
    {
        await Undo(employeeId, compensation, ct);
        var saved = await employees.Save(ct);
        if (saved.IsFailure)
        {
            throw new InvalidOperationException($"Could not record the undoing of the onboarding of {employeeId}: {saved.Error.Messages[0].Code}");
        }
    }

    private async Task Undo(Guid employeeId, Compensation compensation, CancellationToken ct)
    {
        var employee = await employees.GetById(employeeId, ct);
        if (employee.IsSuccess)
        {
            employee.Value.CompensateOnboarding(compensation.Reason);
        }

        outbox.Enqueue(
            EmployeeEventTypes.HireCompensationRequested,
            employeeId.ToString(),
            new HireCompensationRequestedEvent(employeeId, compensation.Reason, compensation.RevokeAccount, compensation.ReverseBonus));
    }

    private async Task Step(Guid employeeId, Func<HireSaga, DateTime, Compensation?> apply, CancellationToken ct)
    {
        var saga = await sagas.Get(employeeId, ct);
        if (saga is null)
        {
            // A hire from before the process existed, or an event for someone this service never hired: nothing to coordinate.
            return;
        }

        var compensation = apply(saga, clock.GetUtcNow().UtcDateTime);
        if (compensation is not null)
        {
            await Undo(employeeId, compensation, ct);
        }

        var saved = await employees.Save(ct);
        if (saved.IsFailure)
        {
            // Another step on the same process committed first (the row's version moved). Nothing of this one was written;
            // failing makes the caller retry, and the retry reads the new state and decides again.
            throw new InvalidOperationException($"Could not record a step of the onboarding of {employeeId}: {saved.Error.Messages[0].Code}");
        }
    }
}
