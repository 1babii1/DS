using System.Globalization;
using System.Text;
using Confluent.Kafka;

namespace Shared.Outbox;

// What goes on the wire for an outbox row, in one place so producers and their tests agree. "occurred-at" is when the
// row was written (the moment the domain change happened), in round-trip UTC form. It is additive: a consumer that does
// not look for it is unaffected, and one that does must cope with its absence (messages from before it existed).
public static class OutboxMessageHeaders
{
    public const string OccurredAt = "occurred-at";

    public static Message<string, string> ToKafkaMessage(OutboxMessage message) => new()
    {
        Key = message.AggregateId,
        Value = message.Payload,
        Headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(message.Id.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(message.Type) },
            { OccurredAt, Encoding.UTF8.GetBytes(message.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) },
        },
    };

    // A header that is missing, malformed or the default date is "no usable time": the caller falls back to its own clock.
    public static bool TryReadOccurredAt(byte[] header, out DateTime occurredAt)
    {
        occurredAt = default;
        if (!DateTime.TryParse(
                Encoding.UTF8.GetString(header),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
            || parsed.Year <= 1)
        {
            return false;
        }

        occurredAt = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
