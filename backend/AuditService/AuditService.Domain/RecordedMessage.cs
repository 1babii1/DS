namespace AuditService.Domain;

// One row per Kafka message the log has taken in, for the whole life of the log. The entries table is partitioned by event
// time (ADR 0027), and Postgres only allows a unique index on a partitioned table if it includes the partition key, so
// "this message id is recorded once" can no longer live on the entries themselves. It lives here instead, written in the
// same transaction as the entry; this table is small (a uuid and a time) and is not partitioned, so dropping old entry
// partitions leaves it behind and a redelivered old message is still recognized.
public class RecordedMessage
{
    public Guid MessageId { get; private set; }

    public DateTime RecordedAt { get; private set; }

    private RecordedMessage()
    {
    }

    public static RecordedMessage Create(Guid messageId) => new()
    {
        MessageId = messageId,
        RecordedAt = DateTime.UtcNow,
    };
}
