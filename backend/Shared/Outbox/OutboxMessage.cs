namespace Shared.Outbox;

// Written in the same DB transaction as the domain change it describes - this is what
// makes the "publish an event" step atomic with the write it accompanies (the classic
// dual-write problem: without this, a crash between "save to DB" and "publish to Kafka"
// silently drops the event, or the reverse ordering publishes an event for a write that
// then fails to commit). A background publisher polls for unprocessed rows and only marks
// them processed after Kafka has acknowledged the produce - so delivery is at-least-once,
// never zero.
public class OutboxMessage
{
    public Guid Id { get; private set; }

    public string Type { get; private set; } = null!;

    public string AggregateId { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public DateTime OccurredAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    private OutboxMessage()
    {
    }

    public static OutboxMessage Create(string type, string aggregateId, string payloadJson) => new()
    {
        // Time-ordered, unlike a v4 Guid - keeps this high-insert-rate table's primary-key
        // index appending at the end of the B-tree instead of scattering writes across
        // random pages. Every other append-only entity in this codebase (Transaction,
        // AuditEntry, DeadLetterEntry, Notification, IdempotencyRecord) was moved the same way.
        Id = Guid.CreateVersion7(),
        Type = type,
        AggregateId = aggregateId,
        Payload = payloadJson,
        OccurredAt = DateTime.UtcNow,
    };

    public void MarkProcessed() => ProcessedAt = DateTime.UtcNow;
}