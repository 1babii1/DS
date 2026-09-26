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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

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
            return HealthCheckResult.Unhealthy("Kafka is not reachable", ex);
        }
    }
}
