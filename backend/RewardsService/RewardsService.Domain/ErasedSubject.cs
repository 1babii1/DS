namespace RewardsService.Domain;

/// <summary>
/// An employee or account id whose ledger was anonymised on request (ADR 0050). Kept so that an event that arrives later for the same id, or
/// one replayed from Kafka, does not start a new wallet under it.
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
