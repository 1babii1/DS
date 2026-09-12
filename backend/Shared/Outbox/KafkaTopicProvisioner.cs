using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Shared.Outbox;

// Kafka's auto-create-on-first-produce/subscribe is unreliable for this exact shape of
// problem: a consumer that subscribes before the topic exists falls back to a slow (default
// 5-minute) metadata refresh interval after a handful of fast retries, and can miss messages
// produced shortly after for a long time even once the topic exists. Explicit provisioning
// before anyone subscribes or produces avoids the whole class of race.
public static class KafkaTopicProvisioner
{
    public static async Task EnsureTopicsExistAsync(string bootstrapServers, params string[] topics)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync(topics.Select(topic => new TopicSpecification
            {
                Name = topic,
                NumPartitions = 1,
                ReplicationFactor = 1,
            }));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Fine - another service instance created it first.
        }
    }
}
