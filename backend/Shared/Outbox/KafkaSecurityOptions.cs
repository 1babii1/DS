using Confluent.Kafka;
using Microsoft.Extensions.Configuration;

namespace Shared.Outbox;

// Both fields empty means "no SASL" - kept optional so Development can still point at a
// plaintext broker if someone runs one outside docker compose. Docker/Production always
// set both, since the broker only accepts SASL_PLAINTEXT connections there.
public record KafkaSecurityOptions(string? SaslUsername, string? SaslPassword)
{
    // How the topics this platform creates are replicated (ADR 0035). They travel with the connection settings because that is
    // what every caller that provisions topics already has in hand. One broker is the default and the development setup;
    // against a cluster, set Kafka:TopicReplicationFactor (3) and Kafka:TopicMinInsyncReplicas (2).
    public int TopicReplicationFactor { get; init; } = 1;

    public int? TopicMinInsyncReplicas { get; init; }

    // How many partitions a topic is created with (ADR 0055). One is the default and what every topic had; a consumer group can use no more
    // consumers than there are partitions, so scaling a consumer past one instance needs this raised before the topic is created.
    public int TopicPartitions { get; init; } = 1;

    public static KafkaSecurityOptions FromConfiguration(IConfiguration configuration) => new(
        configuration["Kafka:SaslUsername"],
        configuration["Kafka:SaslPassword"])
    {
        TopicReplicationFactor = configuration.GetValue("Kafka:TopicReplicationFactor", 1),
        TopicMinInsyncReplicas = configuration.GetValue<int?>("Kafka:TopicMinInsyncReplicas"),
        TopicPartitions = configuration.GetValue("Kafka:TopicPartitions", 1),
    };

    public void ApplyTo(ClientConfig config)
    {
        if (string.IsNullOrWhiteSpace(SaslUsername) || string.IsNullOrWhiteSpace(SaslPassword))
        {
            return;
        }

        config.SecurityProtocol = SecurityProtocol.SaslPlaintext;
        config.SaslMechanism = SaslMechanism.Plain;
        config.SaslUsername = SaslUsername;
        config.SaslPassword = SaslPassword;
    }
}
