using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuditService.Infrastructure;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddAuditInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("AuditServiceDb")
            ?? throw new InvalidOperationException("Connection string 'AuditServiceDb' is not configured.");

        services.AddDbContext<AuditDbContext>(options => options.UseNpgsql(connectionString));

        services.Configure<AuditConsumerOptions>(options =>
        {
            options.BootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
            options.Topics = configuration.GetSection("Kafka:Topics").Get<string[]>()
                ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
            options.GroupId = configuration["Kafka:GroupId"] ?? "audit-service";
        });

        services.AddHostedService<AuditConsumer>();

        return services;
    }
}
