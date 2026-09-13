namespace AuditService.Domain;

// Parking spot for a message that failed every processing attempt. Without this,
// a message that keeps failing (a transient outage that outlasts the retries, or a
// payload ProcessMessage can't handle) was silently skipped forever: the consume
// loop moved on to the next message and its eventual commit advanced the group's
// offset past the failed one, with nothing recording that a message existed there
// at all. That is data loss in a log whose entire point is to be complete.
//
// Unique index on MessageId for the same reason AuditEntry has one: at-least-once
// delivery means this path can run twice for the same message after a restart.
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
        Id = Guid.NewGuid(),
        MessageId = messageId,
        Topic = topic,
        MessageKey = messageKey,
        Payload = payload,
        Error = error,
        AttemptCount = attemptCount,
        FailedAt = DateTime.UtcNow,
    };
}
