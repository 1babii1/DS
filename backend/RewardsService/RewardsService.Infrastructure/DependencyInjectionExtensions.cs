using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure.Consumers;
using Shared.HealthChecks;
using Shared.Outbox;

namespace RewardsService.Infrastructure;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddRewardsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("RewardsServiceDb")
            ?? throw new InvalidOperationException("Connection string 'RewardsServiceDb' is not configured.");

        services.AddDbContext<RewardsDbContext>(options => options.UseNpgsql(connectionString));

        var bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");
        var security = KafkaSecurityOptions.FromConfiguration(configuration);

        services.Configure<WelcomeBonusConsumerOptions>(options =>
        {
            options.BootstrapServers = bootstrapServers;
            options.Security = security;
            options.Topics = configuration.GetSection("Kafka:Topics").Get<string[]>()
                ?? throw new InvalidOperationException("Configuration 'Kafka:Topics' is not set.");
            options.GroupId = configuration["Kafka:GroupId"] ?? "rewards-service";
            options.WelcomeBonusAmount = configuration.GetValue("Rewards:WelcomeBonusAmount", 100m);
        });

        services.AddScoped<CurrencyGrantWriter>();
        services.AddHostedService<WelcomeBonusConsumer>();
        services.AddKafkaHealthCheck(bootstrapServers, security);

        return services;
    }
}
