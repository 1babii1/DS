using EmployeeService.Application.Employees;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmployeeService.Web;

// Time is a fact the onboarding process has to hear about like any other: nothing arrives when an account or a bonus is missing, so
// something has to look. Runs on every instance; each step is idempotent and guarded by the row's version, so two instances
// looking at the same overdue hire cannot both undo it.
public sealed class HireSagaDeadlineWorker(
    IServiceScopeFactory scopes,
    ILogger<HireSagaDeadlineWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var undone = await scope.ServiceProvider.GetRequiredService<HireSagaCoordinator>().CheckDeadlines(stoppingToken);
                if (undone > 0)
                {
                    logger.LogWarning("{Count} hire(s) passed their onboarding deadline and were undone", undone);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A concurrent step on the same row (another instance, or a late event) is the normal reason; the next pass
                // sees the new state.
                logger.LogWarning(ex, "Checking onboarding deadlines failed; will try again");
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
