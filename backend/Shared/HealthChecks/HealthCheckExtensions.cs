using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shared.HealthChecks;

/// <summary>
/// Две разные пробы, потому что на них по-разному реагируют.
///
/// Liveness отвечает на вопрос «поможет ли перезапуск». Недоступная база на него
/// не влияет: перезапуск процесса её не поднимет, а перезапуск всех реплик во время
/// сбоя базы превращает частичную деградацию в полную недоступность. Поэтому проба
/// живости не проверяет ни одной зависимости - только то, что процесс обрабатывает запросы.
///
/// Readiness отвечает на вопрос «можно ли слать сюда трафик» и зависимости проверяет.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>Тег проверок, которые входят в readiness.</summary>
    public const string ReadyTag = "ready";

    public static IHealthChecksBuilder AddDatabaseHealthCheck<TContext>(
        this IServiceCollection services,
        string name = "database")
        where TContext : DbContext =>
        services
            .AddHealthChecks()
            .AddCheck<DatabaseHealthCheck<TContext>>(name, tags: [ReadyTag]);

    public static IEndpointRouteBuilder MapDefaultHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new()
        {
            Predicate = _ => false,
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new()
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
        }).AllowAnonymous();

        return endpoints;
    }
}

public sealed class DatabaseHealthCheck<TContext>(TContext dbContext) : IHealthCheck
    where TContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database is not reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable", ex);
        }
    }
}
