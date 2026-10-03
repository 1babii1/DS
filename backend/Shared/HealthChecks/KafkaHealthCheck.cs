using Confluent.Kafka;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.Outbox;

namespace Shared.HealthChecks;

public static class KafkaHealthCheckExtensions
{
    // AdminClient.GetMetadata is synchronous with no cancellation-token overload -
    // wrapped in Task.Run so a broker that's slow to answer (not just down) doesn't
    // tie up the caller past the timeout passed below.
    public static IHealthChecksBuilder AddKafkaHealthCheck(
        this IServiceCollection services,
        string bootstrapServers,
        KafkaSecurityOptions security,
        string name = "kafka") =>
        services
            .AddHealthChecks()
            .AddCheck(
                name,
                new KafkaHealthCheck(bootstrapServers, security),
                tags: [HealthCheckExtensions.ReadyTag]);
}

public sealed class KafkaHealthCheck(string bootstrapServers, KafkaSecurityOptions security) : IHealthCheck
{
    // Shorter than a Kubernetes readiness probe's timeout (3 s), so the answer arrives in time to be read.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = new AdminClientConfig { BootstrapServers = bootstrapServers };
            security.ApplyTo(config);
            using var admin = new AdminClientBuilder(config).Build();

            await Task.Run(() => admin.GetMetadata(Timeout), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Degraded, not unhealthy, and that is deliberate (ADR 0040). Readiness decides whether traffic is sent here, and no
            // HTTP request of these services needs Kafka: writes land in the outbox and are published when the broker is back.
            // Reporting unhealthy would take every instance of every service out of rotation the moment the broker went away,
            // turning a delay in event delivery into a total outage. Degraded still answers 200 and shows up in the report.
            return HealthCheckResult.Degraded("Kafka is not reachable; events will be delivered late", ex);
        }
    }
}
