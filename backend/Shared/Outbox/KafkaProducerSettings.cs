using Confluent.Kafka;

namespace Shared.Outbox;

public static class KafkaProducerSettings
{
    /// <summary>
    /// A write is acknowledged only once every in-sync replica has it, and a retry after a lost acknowledgement cannot duplicate
    /// it (the broker recognises the producer's sequence numbers): the two together are what let a broker die mid-publish
    /// without a lost or doubled event (ADR 0035).
    /// </summary>
    public static ProducerConfig For(OutboxPublisherOptions options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
        };
        options.Security.ApplyTo(config);
        return config;
    }
}
