using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Outbox;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddOutboxPublisher<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string topic,
        string? avroTopic = null)
        where TContext : DbContext
    {
        services.Configure<OutboxPublisherOptions>(options =>
        {
            var bootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");

            options.BootstrapServers = bootstrapServers;
            options.Security = KafkaSecurityOptions.FromConfiguration(configuration);
            options.Topic = topic;
            options.AvroTopic = avroTopic;
        });

        services.AddHostedService<OutboxPublisher<TContext>>();

        return services;
    }
}