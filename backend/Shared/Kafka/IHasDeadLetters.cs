using Microsoft.EntityFrameworkCore;

namespace Shared.Kafka;

/// <summary>
/// A DbContext that parks unprocessable messages.
/// <para>
/// Exists so <see cref="KafkaRetryConsumer{TDbContext}"/> can reach the dead-letter set
/// through a compile-time contract rather than <c>Set&lt;DeadLetterEntry&gt;()</c>, which
/// only fails once a message has already exhausted its retries in production - the single
/// moment the mechanism is most needed. A service that wires up a consumer without mapping
/// the entity now fails to build instead.
/// </para>
/// </summary>
public interface IHasDeadLetters
{
    DbSet<DeadLetterEntry> DeadLetters { get; }
}
