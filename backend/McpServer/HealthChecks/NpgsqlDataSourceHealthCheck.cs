using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace McpServer.HealthChecks;

/// <summary>
/// McpServer подключается через NpgsqlDataSource напрямую (Dapper), а не через
/// DbContext, поэтому обычная EF-проверка здесь не подходит.
/// </summary>
public sealed class NpgsqlDataSourceHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable", ex);
        }
    }
}
