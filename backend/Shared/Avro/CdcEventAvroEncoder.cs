namespace Shared.Avro;

/// <summary>Thrown when an event cannot be staged as Avro in CDC mode: the write is refused rather than leaving a row nobody can publish.</summary>
public sealed class EventNotEncodableException(string eventType)
    : InvalidOperationException($"Event '{eventType}' could not be encoded as Avro (the schema registry has not been reached yet), and in CDC mode a row without its Avro bytes cannot be published")
{
}

/// <summary>
/// The encoder of a service whose outbox is read by Debezium (ADR 0030). Debezium relays the bytes in the row's AvroPayload
/// column untouched and cannot make them from the JSON as the polling publisher can, so a row written without them would never
/// leave. Staging therefore either succeeds or the write fails. Once the schemas have been registered the ids are in memory and
/// this never fails again; the window is a service that has not yet reached the registry since it started.
/// </summary>
public sealed class CdcEventAvroEncoder(IEventAvroEncoder inner) : IEventAvroEncoder
{
    public bool IsConfigured => inner.IsConfigured;

    public bool IsReady => inner.IsReady;

    public bool DeliveredByCdc => true;

    public bool TryEncode(string eventType, object payload, out byte[] bytes) =>
        inner.TryEncode(eventType, payload, out bytes) ? true : throw new EventNotEncodableException(eventType);

    public Task<byte[]> EncodeJsonAsync(string eventType, string json, CancellationToken cancellationToken) =>
        inner.EncodeJsonAsync(eventType, json, cancellationToken);
}
