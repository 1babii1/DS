using EmployeeService.Application.Database;
using EmployeeService.Domain;
using Microsoft.Extensions.Options;

namespace EmployeeService.Application.Employees;

public enum HireOrchestrationMode
{
    /// <summary>The saga of ADR 0032: a row per hire in Postgres, a worker that looks at the deadlines.</summary>
    Saga = 0,

    /// <summary>The same process as a Temporal workflow (ADR 0056): the state, the timer and the retries are Temporal's.</summary>
    Temporal = 1,
}

public sealed class HireOrchestrationOptions
{
    public const string SectionName = "HireOrchestration";

    public HireOrchestrationMode Mode { get; set; } = HireOrchestrationMode.Saga;

    public string Address { get; set; } = "localhost:7233";

    public string Namespace { get; set; } = "default";

    public string TaskQueue { get; set; } = "hire-onboarding";

    /// <summary>How long the workflow stays to hear of a step that finishes after it decided to undo the hire. The saga hears of it whenever it comes.</summary>
    public TimeSpan LateStepGrace { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// What carries a hire through its onboarding: it starts with the hire, hears the facts the other services report, and undoes the hire
/// if they do not all arrive in time. One process, two implementations (ADR 0056), chosen by <see cref="HireOrchestrationOptions.Mode"/>.
/// Every method may be called more than once for the same fact; events arrive at least once.
/// </summary>
public interface IHireOrchestrator
{
    /// <summary>Called inside the hire's own transaction, before it commits.</summary>
    Task BeginAsync(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>Called once the hire has committed. Whatever cannot take part in the transaction starts here.</summary>
    Task AfterCommitAsync(Guid employeeId, CancellationToken cancellationToken);

    Task AccountProvisionedAsync(Guid employeeId, CancellationToken cancellationToken);

    Task BonusGrantedAsync(Guid employeeId, CancellationToken cancellationToken);

    Task AccountProvisioningFailedAsync(Guid employeeId, string reason, CancellationToken cancellationToken);
}

/// <summary>The process of ADR 0032: its state is a row written in the hire's transaction, and the facts go to the coordinator.</summary>
public sealed class SagaHireOrchestrator(
    IHireSagaRepository sagas,
    HireSagaCoordinator coordinator,
    IOptions<HireSagaOptions> options,
    TimeProvider clock) : IHireOrchestrator
{
    public async Task BeginAsync(Guid employeeId, CancellationToken cancellationToken) =>
        await sagas.Add(HireSaga.Start(employeeId, clock.GetUtcNow().UtcDateTime, options.Value.OnboardingTimeout), cancellationToken);

    public Task AfterCommitAsync(Guid employeeId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AccountProvisionedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        coordinator.OnAccountProvisioned(employeeId, cancellationToken);

    public Task BonusGrantedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        coordinator.OnBonusGranted(employeeId, cancellationToken);

    public Task AccountProvisioningFailedAsync(Guid employeeId, string reason, CancellationToken cancellationToken) =>
        coordinator.OnAccountProvisioningFailed(employeeId, reason, cancellationToken);
}
