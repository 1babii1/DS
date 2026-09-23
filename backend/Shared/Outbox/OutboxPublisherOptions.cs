namespace Shared.Outbox;

public class OutboxPublisherOptions
{
    public string BootstrapServers { get; set; } = null!;

    public KafkaSecurityOptions Security { get; set; } = new(null, null);

    public string Topic { get; set; } = null!;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int BatchSize { get; set; } = 20;

    // Isolated failures before a message is parked for an operator. See OutboxBatch.
    public int MaxAttempts { get; set; } = 10;
}