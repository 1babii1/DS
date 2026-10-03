namespace AuditService.Domain;

/// <summary>
/// The key that the personal fields about one subject (an account, an employee, or an address that tried to sign in) are encrypted
/// under (ADR 0046). Wrapped by the master key. Erasing the subject empties <see cref="WrappedKey"/> and keeps the row, so that an
/// event arriving later for an erased subject is stored redacted instead of getting a fresh key.
/// </summary>
public class SubjectKey
{
    public string SubjectId { get; private set; } = null!;

    public byte[] WrappedKey { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public DateTime? ErasedAt { get; private set; }

    public bool IsErased => ErasedAt is not null;
}
