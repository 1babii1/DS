namespace AuditService.Domain;

// Append-only: rows are never updated or deleted. MessageId is the Kafka outbox
// message id set by the producer - the unique index on it is what makes writing
// an entry idempotent under at-least-once delivery.
public class AuditEntry
{
    public Guid Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string SourceService { get; private set; } = null!;

    public string EventType { get; private set; } = null!;

    public string AggregateId { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public DateTime OccurredAt { get; private set; }

    public DateTime ReceivedAt { get; private set; }

    // The id of the Avro schema the event was written with, when it arrived on an Avro topic (ADR 0023); null for JSON.
    // The Payload is always JSON, so the log outlives the schema registry.
    public int? SchemaId { get; private set; }

    private AuditEntry()
    {
    }

    public static AuditEntry Create(
        Guid messageId,
        string sourceService,
        string eventType,
        string aggregateId,
        string payloadJson,
        DateTime occurredAt,
        int? schemaId = null) => new()
        {
            Id = Guid.CreateVersion7(),
            MessageId = messageId,
            SourceService = sourceService,
            EventType = eventType,
            AggregateId = aggregateId,
            Payload = payloadJson,
            OccurredAt = occurredAt,
            ReceivedAt = DateTime.UtcNow,
            SchemaId = schemaId,
        };
}