using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SearchService.Infrastructure.Elasticsearch;

public sealed class ElasticsearchHealthCheck(ElasticsearchClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.PingAsync(cancellationToken);
            return response.IsValidResponse
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Elasticsearch is not reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Elasticsearch is not reachable", ex);
        }
    }
}
