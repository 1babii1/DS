namespace Shared.Kafka;

/// <summary>
/// A message that exhausted every processing attempt, parked instead of being silently
/// skipped past by the consumer's own offset commit.
/// <para>
/// Lives in Shared for the same reason <see cref="Shared.Outbox.OutboxMessage"/> does: the
/// shape is mechanism, not domain, and six services had byte-identical copies of it. The
/// row still belongs to each service - every DbContext maps this into its own schema, so
/// schema-per-service is untouched; only the class is shared.
/// </para>
/// </summary>
public class DeadLetterEntry
{
    public Guid Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string Topic { get; private set; } = null!;

    public string MessageKey { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public string Error { get; private set; } = null!;

    public int AttemptCount { get; private set; }

    public DateTime FailedAt { get; private set; }

    private DeadLetterEntry()
    {
    }

    public static DeadLetterEntry Create(
        Guid messageId,
        string topic,
        string messageKey,
        string payload,
        string error,
        int attemptCount) => new()
        {
            Id = Guid.CreateVersion7(),
            MessageId = messageId,
            Topic = topic,
            MessageKey = messageKey,
            Payload = payload,
            Error = error,
            AttemptCount = attemptCount,
            FailedAt = DateTime.UtcNow,
        };
}
