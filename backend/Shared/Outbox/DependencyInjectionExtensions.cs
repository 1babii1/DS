using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Outbox;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddOutboxPublisher<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string topic)
        where TContext : DbContext
    {
        services.Configure<OutboxPublisherOptions>(options =>
        {
            var bootstrapServers = configuration["Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Configuration 'Kafka:BootstrapServers' is not set.");

            options.BootstrapServers = bootstrapServers;
            options.Security = KafkaSecurityOptions.FromConfiguration(configuration);
            options.Topic = topic;
            options.BatchSize = configuration.GetValue("Outbox:BatchSize", options.BatchSize);
            options.PollInterval = configuration.GetValue("Outbox:PollInterval", options.PollInterval);
        });

        if (configuration.OutboxMode() == OutboxMode.Cdc)
        {
            // Debezium delivers the rows; this process only clears out the ones that are long gone (nothing marks them in a way
            // a table scan would find, and they would otherwise grow without end).
            services.AddHostedService<OutboxCleaner<TContext>>();
            return services;
        }

        services.AddHostedService<OutboxPublisher<TContext>>();

        return services;
    }
}