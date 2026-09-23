using Microsoft.Extensions.Logging;

namespace Shared.Outbox;

public record OutboxBatchResult(int Succeeded, int Failed, int Parked);

// The decision logic of one publish cycle, separate from Kafka and the database so it can be
// tested without either. Each message is isolated: one that keeps failing must not hold up
// the ones queued behind it, which is what a single try around the whole batch used to do.
public static class OutboxBatch
{
    public static async Task<OutboxBatchResult> ProcessAsync(
        IReadOnlyList<OutboxMessage> pending,
        Func<OutboxMessage, CancellationToken, Task> publish,
        int maxAttempts,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var succeeded = 0;
        var failures = new List<(OutboxMessage Message, Exception Error)>();

        foreach (var message in pending)
        {
            try
            {
                await publish(message, cancellationToken);
                message.MarkProcessed();
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add((message, ex));
            }
        }

        // A failure only counts toward parking when something else in the same batch went
        // through. If everything failed, the cause is the broker or the network, not the
        // messages - counting it would park perfectly healthy messages during an outage.
        var parked = 0;
        foreach (var (message, error) in failures)
        {
            if (succeeded > 0)
            {
                message.RecordFailure(error.Message, maxAttempts);
                if (message.ParkedAt is not null)
                {
                    parked++;
                    logger.LogError(
                        error,
                        "Outbox message {MessageId} ({MessageType}) parked after {Attempts} failed attempts",
                        message.Id,
                        message.Type,
                        message.AttemptCount);
                    continue;
                }
            }

            logger.LogWarning(
                error,
                "Failed to publish outbox message {MessageId} ({MessageType}) - will retry next poll",
                message.Id,
                message.Type);
        }

        return new OutboxBatchResult(succeeded, failures.Count, parked);
    }
}
