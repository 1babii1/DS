namespace RewardsService.Domain;

// Backs the Idempotency-Key header on POST /api/rewards/grants (IETF
// draft-ietf-httpapi-idempotency-key-header): a retried or double-clicked grant request
// must return the original result, not create a second Transaction. (Scope, Key) is
// enforced unique in the database, not just checked-then-inserted - two requests racing on
// the same key both attempt the insert, and the loser's whole SaveChanges (wallet update +
// transaction + outbox message + this row, all in one call) rolls back, so it can safely
// re-read and return the winner's TransactionId instead of leaving a partial grant behind.
public class IdempotencyRecord
{
    public Guid Id { get; private set; }

    public string Scope { get; private set; } = null!;

    public string Key { get; private set; } = null!;

    // SHA-256 of the request body that produced TransactionId. A caller reusing the same
    // key for a genuinely different request (a bug, not a retry) gets a conflict instead of
    // silently receiving someone else's grant result.
    public string RequestHash { get; private set; } = null!;

    public Guid TransactionId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private IdempotencyRecord()
    {
    }

    public static IdempotencyRecord Create(string scope, string key, string requestHash, Guid transactionId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Scope = scope,
            Key = key,
            RequestHash = requestHash,
            TransactionId = transactionId,
            CreatedAt = DateTime.UtcNow,
        };
}
