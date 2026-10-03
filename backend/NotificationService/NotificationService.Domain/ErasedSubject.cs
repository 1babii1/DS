namespace NotificationService.Domain;

/// <summary>
/// A subject (an account id or an employee id) whose personal data this service was asked to erase (ADR 0047). Kept so that an event that
/// arrives later for the same subject, or one replayed from Kafka, does not bring the data back.
/// </summary>
public class ErasedSubject
{
    public string SubjectId { get; private set; } = null!;

    public DateTime ErasedAt { get; private set; }

    private ErasedSubject()
    {
    }

    public static ErasedSubject Create(string subjectId) => new() { SubjectId = subjectId, ErasedAt = DateTime.UtcNow };
}
