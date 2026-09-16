namespace NotificationService.Domain;

// One row per in-app notification, persisted regardless of whether the recipient is
// connected to the SignalR hub right now - a push is best-effort delivery on top of this,
// never the only copy. RecipientAccountId is the "sub" claim, the same identity every
// other service in the platform keys its authorization on.
public class Notification
{
    public Guid Id { get; private set; }

    // The outbox message-id that caused this notification - not the notification's own
    // Id. Redelivery of the same Kafka message must not create a second notification;
    // this is what the consumer's idempotency check (and the unique index backing it)
    // keys on, the same role MessageId plays on DeadLetterEntry/AuditEntry.
    public Guid SourceMessageId { get; private set; }

    public Guid RecipientAccountId { get; private set; }

    public string Type { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    // Where the frontend should navigate on click - optional because not every
    // notification type has a natural destination (e.g. a welcome bonus).
    public string? DeepLink { get; private set; }

    public bool IsRead { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? ReadAt { get; private set; }

    private Notification()
    {
    }

    public static Notification Create(
        Guid sourceMessageId,
        Guid recipientAccountId,
        string type,
        string title,
        string body,
        string? deepLink = null) => new()
        {
            Id = Guid.NewGuid(),
            SourceMessageId = sourceMessageId,
            RecipientAccountId = recipientAccountId,
            Type = type,
            Title = title,
            Body = body,
            DeepLink = deepLink,
            IsRead = false,
            CreatedAt = DateTime.UtcNow,
        };

    // Idempotent on purpose: "mark read" firing twice (a double click, a retried
    // request) is a no-op, not an error - ReadAt should reflect the first read, not the
    // most recent request.
    public void MarkRead()
    {
        if (IsRead)
        {
            return;
        }

        IsRead = true;
        ReadAt = DateTime.UtcNow;
    }
}
