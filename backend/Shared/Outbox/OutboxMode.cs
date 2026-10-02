namespace Shared.Outbox;

public enum OutboxMode
{
    /// <summary>The service's own publisher polls the outbox table and produces to Kafka (the default).</summary>
    Polling,

    /// <summary>Debezium reads the outbox table's write-ahead log and produces to Kafka; the service publishes nothing itself (ADR 0030).</summary>
    Cdc,
}

public static class OutboxModeExtensions
{
    public const string ConfigurationKey = "Outbox:Mode";

    public static OutboxMode OutboxMode(this Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        Enum.TryParse<OutboxMode>(configuration[ConfigurationKey], ignoreCase: true, out var mode) ? mode : Outbox.OutboxMode.Polling;
}
