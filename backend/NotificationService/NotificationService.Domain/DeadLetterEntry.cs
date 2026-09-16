namespace NotificationService.Domain;

// Same shape and same reason as AuditService.Domain.DeadLetterEntry / EmployeeService's /
// RewardsService's - a message that exhausts every processing attempt is parked here
// instead of silently skipped past by the consumer's own offset commit. Unique index on
// MessageId for the same reason: at-least-once delivery means this path can run twice for
// the same message after a restart.
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
