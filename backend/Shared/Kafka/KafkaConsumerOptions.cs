using Shared.Outbox;

namespace Shared.Kafka;

/// <summary>
/// What every consumer in the platform needs to attach to Kafka. Services that need more
/// (a welcome-bonus amount, say) derive from this rather than redeclaring these four.
/// </summary>
public class KafkaConsumerOptions
{
    public string BootstrapServers { get; set; } = null!;

    public KafkaSecurityOptions Security { get; set; } = new(null, null);

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = null!;
}
