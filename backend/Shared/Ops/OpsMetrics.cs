using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Kafka;
using Shared.Outbox;

namespace Shared.Ops;

public record OpsSnapshot(int Parked, int Pending, double OldestPendingAgeSeconds, int? DeadLetters);

// Reads the numbers the alerts are built on. Separate from the reporter so it is tested
// against a real database without a metrics pipeline.
public static class OpsSnapshotReader
{
    public static async Task<OpsSnapshot> ReadAsync(DbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var parked = 0;
        var pending = 0;
        DateTime? oldest = null;

        // Consumer-only services (Audit, Notification, Search) have no outbox table at all.
        if (db.Model.FindEntityType(typeof(OutboxMessage)) is not null)
        {
            var outbox = db.Set<OutboxMessage>().AsNoTracking();

            // Parked messages are excluded from "pending": they have their own signal, and counting
            // them as backlog would make one stuck message look like a slow publisher.
            var pendingQuery = outbox.Where(m => m.ProcessedAt == null && m.ParkedAt == null);
            pending = await pendingQuery.CountAsync(ct);
            parked = await outbox.CountAsync(m => m.ParkedAt != null, ct);
            oldest = await pendingQuery.MinAsync(m => (DateTime?)m.OccurredAt, ct);
        }

        int? deadLetters = db is IHasDeadLetters withDeadLetters
            ? await withDeadLetters.DeadLetters.CountAsync(ct)
            : null;

        return new OpsSnapshot(parked, pending, oldest is null ? 0 : (nowUtc - oldest.Value).TotalSeconds, deadLetters);
    }
}

// Publishes the snapshot as gauges on the service's own meter (already exported: see
// ObservabilityExtensions.AddMeter). Polls on a timer rather than querying inside the gauge
// callback, so a slow database can never stall metric export.
public sealed class OpsMetricsReporter<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<OpsMetricsReporter<TContext>> logger,
    string meterName)
    : BackgroundService
    where TContext : DbContext
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Meter _meter = new(meterName);
    private volatile OpsSnapshot _latest = new(0, 0, 0, null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.CreateObservableGauge("outbox_parked_messages", () => _latest.Parked,
            description: "Outbox messages parked after repeated publish failures; need an operator");
        _meter.CreateObservableGauge("outbox_pending_messages", () => _latest.Pending,
            description: "Outbox messages waiting to be published");
        // No unit argument on purpose: the name already ends in _seconds, and a unit would make the
        // Prometheus exporter risk appending it again, silently breaking the alert that reads it.
        _meter.CreateObservableGauge("outbox_oldest_pending_age_seconds", () => _latest.OldestPendingAgeSeconds,
            description: "Age in seconds of the oldest unpublished, unparked outbox message");
        _meter.CreateObservableGauge("dead_letters", () => _latest.DeadLetters ?? 0,
            description: "Consumed messages that exhausted retries and were parked in dead_letters");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TContext>();
                _latest = await OpsSnapshotReader.ReadAsync(db, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the last known values; an unreachable database is already someone else's alert.
                logger.LogWarning(ex, "Could not refresh ops metrics");
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

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }
}

public static class OpsMetricsExtensions
{
    public static IServiceCollection AddOpsMetrics<TContext>(this IServiceCollection services, string serviceName)
        where TContext : DbContext
    {
        services.AddHostedService(sp => new OpsMetricsReporter<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<OpsMetricsReporter<TContext>>>(),
            serviceName));
        return services;
    }
}
