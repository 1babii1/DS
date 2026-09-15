using Confluent.Kafka;
using Microsoft.Extensions.Configuration;

namespace Shared.Outbox;

// Both fields empty means "no SASL" - kept optional so Development can still point at a
// plaintext broker if someone runs one outside docker compose. Docker/Production always
// set both, since the broker only accepts SASL_PLAINTEXT connections there.
public record KafkaSecurityOptions(string? SaslUsername, string? SaslPassword)
{
    public static KafkaSecurityOptions FromConfiguration(IConfiguration configuration) => new(
        configuration["Kafka:SaslUsername"],
        configuration["Kafka:SaslPassword"]);

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
