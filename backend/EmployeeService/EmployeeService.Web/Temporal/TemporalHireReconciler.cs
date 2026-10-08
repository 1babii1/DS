using EmployeeService.Domain;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace EmployeeService.Web.Temporal;

/// <summary>
/// Closes the gap that starting a workflow after the hire's commit leaves: the commit can succeed and the call to Temporal fail, and then a
/// hire waits for an account with nothing watching its deadline. Every employee still waiting for an account has a workflow; the start is
/// idempotent (the workflow is named after the employee), so looking again does no harm. The saga had no such gap because its state was written
/// in the hire's own transaction; this is what moving the state out of the database costs (ADR 0056).
/// </summary>
public sealed class TemporalHireReconciler(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<TemporalHireReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    // A hire that has only just committed is being started by its own request; leave it a moment.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<TemporalHireOrchestrator>();

                var cutoff = clock.GetUtcNow().UtcDateTime - Settle;
                var waiting = await db.Set<Employee>().AsNoTracking()
                    .Where(e => e.Status == EmployeeStatus.PendingProvisioning && e.CreatedAt < cutoff)
                    .Select(e => new { e.Id, e.CreatedAt })
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var employee in waiting)
                {
                    await orchestrator.StartAsync(employee.Id, signal: null, hiredAt: employee.CreatedAt, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Starting workflows for hires that are still waiting failed; will try again");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
