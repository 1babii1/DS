using System.Linq.Expressions;
using Shared.Avro;

namespace Shared.Outbox;

// What one publish cycle does with one outbox row while an event goes to two topics (ADR 0023): the JSON to the old
// topic and the Avro to the new one. The two sides are tracked apart (ProcessedAt, AvroPublishedAt), so a failure on
// one side never re-sends the other: the JSON side is marked done before the Avro side is tried, and a row that is only
// owed its Avro side comes back on the next poll for just that. The transport is passed in, so this runs in tests
// without Kafka.
public static class OutboxRowPublisher
{
    public static async Task PublishAsync(
        OutboxMessage message,
        Func<OutboxMessage, CancellationToken, Task> sendJson,
        Func<OutboxMessage, byte[], CancellationToken, Task> sendAvro,
        IEventAvroEncoder? avro,
        string? avroTopic,
        CancellationToken cancellationToken)
    {
        if (message.ProcessedAt is null)
        {
            await sendJson(message, cancellationToken);
            message.MarkProcessed();
        }

        if (AvroOwed(message, avro, avroTopic))
        {
            var bytes = message.AvroPayload ?? await avro!.EncodeJsonAsync(message.Type, message.Payload, cancellationToken);
            await sendAvro(message, bytes, cancellationToken);
            message.MarkAvroPublished();
        }
    }

    private static bool AvroOwed(OutboxMessage message, IEventAvroEncoder? avro, string? avroTopic) =>
        avroTopic is not null && avro is { IsConfigured: true } && message.AvroExpected && message.AvroPublishedAt is null;

    /// <summary>The rows a cycle should pick up: not parked, and still owed to the old topic or, when Avro is on, to the new one.</summary>
    public static Expression<Func<OutboxMessage, bool>> Pending(bool avroOn) => avroOn
        ? m => m.ParkedAt == null && (m.ProcessedAt == null || (m.AvroExpected && m.AvroPublishedAt == null))
        : m => m.ParkedAt == null && m.ProcessedAt == null;
}
