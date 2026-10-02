using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuditService.Infrastructure;

public sealed class AuditPartitionOptions
{
    public const string SectionName = "AuditPartitions";

    /// <summary>How many months beyond the current one are kept ready, so an event is never routed to the default partition in normal running.</summary>
    public int MonthsAhead { get; set; } = 3;

    /// <summary>
    /// Entries older than this many months are dropped, a whole partition at a time. <c>0</c> (the default) keeps everything.
    /// The org time machine replays the log from its start, so any value here shortens how far back it can answer (ADR 0027).
    /// </summary>
    public int RetentionMonths { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}

// Keeps the partitions of audit.entries ahead of the clock, and applies retention when it is configured. Runs on every
// instance; the work is idempotent, so two instances doing it at once is harmless.
public sealed class AuditPartitionMaintainer(
    IServiceScopeFactory scopes,
    IOptions<AuditPartitionOptions> options,
    TimeProvider clock,
    ILogger<AuditPartitionMaintainer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure here must not take the service down; the next pass tries again, and the default partition
                // catches anything that arrives in the meantime.
                logger.LogError(ex, "Maintaining the audit partitions failed; will try again");
            }

            try
            {
                await Task.Delay(options.Value.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        await AuditPartitions.EnsureAheadAsync(db, now, settings.MonthsAhead, cancellationToken);

        if (settings.RetentionMonths > 0)
        {
            var dropped = await AuditPartitions.DropOlderThanAsync(db, now.AddMonths(-settings.RetentionMonths), cancellationToken);
            if (dropped.Count > 0)
            {
                logger.LogWarning("Retention dropped {Count} audit partition(s): {Partitions}", dropped.Count, string.Join(", ", dropped));
            }
        }
    }
}
