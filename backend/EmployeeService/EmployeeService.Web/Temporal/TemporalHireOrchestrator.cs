using EmployeeService.Application.Database;
using EmployeeService.Application.Employees;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace EmployeeService.Web.Temporal;

/// <summary>
/// The onboarding as a Temporal workflow, one per hire, named after the employee (ADR 0056). Every call is "start it if it is not there, and
/// tell it this": a signal sent with the start, so a fact that arrives before the workflow exists, or after a start that failed, is not lost.
/// A workflow that has already finished is left alone: the process is over, and a repeated fact is nothing to act on.
/// </summary>
public sealed class TemporalHireOrchestrator(
    ITemporalClient client,
    IOptions<HireOrchestrationOptions> options,
    IOptions<HireSagaOptions> saga,
    IEmployeeRepository employees,
    ILogger<TemporalHireOrchestrator> logger) : IHireOrchestrator
{
    public static string WorkflowId(Guid employeeId) => $"hire-{employeeId:N}";

    // The hire's transaction has nothing to add: the state is Temporal's.
    public Task BeginAsync(Guid employeeId, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task AfterCommitAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        try
        {
            await StartAsync(employeeId, signal: null, hiredAt: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The hire has committed and Temporal could not be told. Not a reason to fail the hire: the reconciler starts the workflow for
            // every employee still waiting for an account (see TemporalHireReconciler).
            logger.LogWarning(ex, "The hire of {EmployeeId} committed but its workflow could not be started; the reconciler will start it", employeeId);
        }
    }

    public Task AccountProvisionedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        StartAsync(employeeId, HireWorkflow.AccountProvisionedSignal, hiredAt: null, cancellationToken);

    public Task BonusGrantedAsync(Guid employeeId, CancellationToken cancellationToken) =>
        StartAsync(employeeId, HireWorkflow.BonusGrantedSignal, hiredAt: null, cancellationToken);

    public async Task AccountProvisioningFailedAsync(Guid employeeId, string reason, CancellationToken cancellationToken)
    {
        var workflowOptions = Options(employeeId);
        workflowOptions.StartSignal = HireWorkflow.AccountProvisioningFailedSignal;
        workflowOptions.StartSignalArgs = [reason];
        await Send(employeeId, workflowOptions, hiredAt: null, cancellationToken);
    }

    /// <summary>Starts the workflow if there is none (and signals it if given); a running one is signalled, a finished one is left.</summary>
    public Task StartAsync(Guid employeeId, string? signal, DateTime? hiredAt, CancellationToken cancellationToken)
    {
        var workflowOptions = Options(employeeId);
        if (signal is not null)
        {
            workflowOptions.StartSignal = signal;
        }

        return Send(employeeId, workflowOptions, hiredAt, cancellationToken);
    }

    private WorkflowOptions Options(Guid employeeId) => new(WorkflowId(employeeId), options.Value.TaskQueue)
    {
        // A second start while one is running attaches to it; a start after it has finished is refused (and ignored below).
        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
        IdReusePolicy = WorkflowIdReusePolicy.RejectDuplicate,
    };

    private async Task Send(Guid employeeId, WorkflowOptions workflowOptions, DateTime? hiredAt, CancellationToken cancellationToken)
    {
        // The deadline counts from the hire, so a workflow that is started late (Temporal was down, or the first fact to arrive started it)
        // does not give the hire more time than the saga would have.
        if (hiredAt is null)
        {
            var employee = await employees.GetById(employeeId, cancellationToken);
            hiredAt = employee.IsSuccess ? employee.Value.CreatedAt : null;
        }

        var input = new HireWorkflowInput(employeeId, saga.Value.OnboardingTimeout, options.Value.LateStepGrace, hiredAt);
        try
        {
            await client.StartWorkflowAsync((HireWorkflow workflow) => workflow.RunAsync(input), workflowOptions);
        }
        catch (WorkflowAlreadyStartedException)
        {
            // It ran and finished: the hire is complete or has been undone, and this fact changes nothing.
        }
    }
}
