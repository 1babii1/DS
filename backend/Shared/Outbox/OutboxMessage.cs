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

    // The same event in the Confluent Avro wire format (ADR 0023), staged in the same transaction as the JSON when the
    // schema registry was reachable. Null otherwise; the JSON payload is always there during the migration.
    public byte[]? AvroPayload { get; private set; }

    // True when this row is meant to reach the Avro topic as well (set when the row is written and an encoder is
    // configured), whether or not the bytes could be made then. Rows written before that never are, so they are not
    // sent twice by accident when the Avro topic is switched on.
    public bool AvroExpected { get; private set; }

    public DateTime? AvroPublishedAt { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public DateTime? ProcessedAt { get; private set; }

    // Publish failures that counted toward parking (see OutboxBatch for which ones count).
    public int AttemptCount { get; private set; }

    public string? LastError { get; private set; }

    // Set when a message has failed too often to keep retrying silently. A parked message
    // is skipped by the publisher and surfaced to an operator; Redrive() puts it back.
    public DateTime? ParkedAt { get; private set; }

    public const int MaxErrorLength = 2000;

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

    // Called by a writer whose encoder is configured: the row is owed to the Avro topic; the bytes are there when the
    // schema ids were known at write time, and are made from the JSON at publish time when they were not.
    public void ExpectAvro(byte[]? avro)
    {
        AvroExpected = true;
        AvroPayload = avro;
    }

    // The first time wins: the dual publisher marks the JSON side done before it tries the Avro side, and the batch
    // marks the row again when both are through.
    public void MarkProcessed() => ProcessedAt ??= DateTime.UtcNow;

    public void MarkAvroPublished() => AvroPublishedAt ??= DateTime.UtcNow;

    public void RecordFailure(string error, int maxAttempts)
    {
        AttemptCount++;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        if (AttemptCount >= maxAttempts)
        {
            ParkedAt = DateTime.UtcNow;
        }
    }

    public void Redrive()
    {
        AttemptCount = 0;
        ParkedAt = null;
    }
}