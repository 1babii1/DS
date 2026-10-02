using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shared.Outbox;

// In CDC mode (ADR 0030) Debezium reads the write-ahead log, not the table, so a row can be deleted as soon as it has committed
// and the connector has had time to read it. Rows are kept for a while anyway: a person looking at the outbox (scripts/outbox.sh)
// sees what was written, and a connector that was stopped for a day still has its position in the log, not in this table.
public sealed class OutboxCleaner<TContext>(IServiceScopeFactory scopes, ILogger<OutboxCleaner<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                var cutoff = DateTime.UtcNow - Keep;
                var deleted = await db.Set<OutboxMessage>().Where(m => m.ProcessedAt != null && m.OccurredAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Removed {Count} delivered outbox row(s) older than {Keep}", deleted, Keep);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Cleaning the outbox failed; will try again");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
