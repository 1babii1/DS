namespace EmployeeService.Domain;

/// <summary>
/// Backs the Idempotency-Key header on POST /api/employees (ADR 0037): a retried or double-clicked hire returns the original
/// result instead of failing on its own email or creating a second person. (Scope, Key) is unique in the database, and the
/// record is written in the same transaction as the hire, so either both exist or neither does.
/// </summary>
public class IdempotencyRecord
{
    public Guid Id { get; private set; }

    public string Scope { get; private set; } = null!;

    public string Key { get; private set; } = null!;

    /// <summary>A hash of the request that produced the result. The same key with a different request is a caller bug, not a retry.</summary>
    public string RequestHash { get; private set; } = null!;

    public Guid ResultId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private IdempotencyRecord()
    {
    }

    public static IdempotencyRecord Create(string scope, string key, string requestHash, Guid resultId, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        Scope = scope,
        Key = key,
        RequestHash = requestHash,
        ResultId = resultId,
        CreatedAt = now,
    };
}
