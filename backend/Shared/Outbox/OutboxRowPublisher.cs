using Shared.Avro;

namespace Shared.Outbox;

// What one publish cycle does with one outbox row (ADR 0023): the event goes to its Avro topic, from the bytes staged when
// the row was written or, when the registry was unreachable then, made from the stored JSON now. The row is marked done
// only after the send, so a failure anywhere (registry, broker) leaves it pending for the next poll. The transport is
// passed in, so this runs in tests without Kafka.
public static class OutboxRowPublisher
{
    public static async Task PublishAsync(
        OutboxMessage message,
        Func<OutboxMessage, byte[], CancellationToken, Task> send,
        IEventAvroEncoder avro,
        CancellationToken cancellationToken)
    {
        var bytes = message.AvroPayload ?? await avro.EncodeJsonAsync(message.Type, message.Payload, cancellationToken);
        await send(message, bytes, cancellationToken);
        message.MarkProcessed();
    }
}
