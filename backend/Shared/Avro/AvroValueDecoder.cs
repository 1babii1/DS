using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Avro;
using Avro.Generic;
using Avro.IO;
using Confluent.SchemaRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shared.Avro;

public sealed record DecodedEvent(string Json, int SchemaId);

/// <summary>Turns the bytes of an Avro-topic message back into the JSON the consumers' record copies understand.</summary>
public interface IEventAvroDecoder
{
    DecodedEvent Decode(byte[] bytes, string? eventType);
}

/// <summary>
/// Reads the Confluent wire format (a zero byte, the writer's schema id, the Avro body) and resolves it to the schema the
/// consumer asked for: when the consumer declared a reader schema for the event, Avro's own resolution applies (fields it
/// did not declare are dropped, fields the producer's version lacks take the reader's defaults); with none, the writer's
/// whole record is returned (what AuditService wants, since it keeps every field). The result is the event as JSON in the
/// shape System.Text.Json gives the records the consumers already have, so their handling does not change.
/// </summary>
public sealed class AvroValueDecoder(ISchemaRegistryClient registry, EventSchemaCatalog readers) : IEventAvroDecoder
{
    private readonly ConcurrentDictionary<int, global::Avro.Schema> _writers = new();

    public DecodedEvent Decode(byte[] bytes, string? eventType)
    {
        if (bytes.Length < 5 || bytes[0] != 0)
        {
            throw new FormatException("Not a Confluent Avro message (missing the zero magic byte or the schema id)");
        }

        var id = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4));
        var writer = (RecordSchema)_writers.GetOrAdd(id, key =>
            global::Avro.Schema.Parse(registry.GetSchemaAsync(key).GetAwaiter().GetResult().SchemaString));

        // Resolved by what the writer's record is called; the header's message-type is only a fallback.
        var name = writer.Name;
        var reader = readers.TryGet(name, out var wanted, out _) || (eventType is not null && readers.TryGet(eventType, out wanted, out _))
            ? wanted
            : writer;

        using var stream = new MemoryStream(bytes, 5, bytes.Length - 5);
        var record = new GenericDatumReader<GenericRecord>(writer, reader).Read(null!, new BinaryDecoder(stream));
        return new DecodedEvent(ToJson(record), id);
    }

    internal static string ToJson(GenericRecord record)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            WriteRecord(json, record);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRecord(Utf8JsonWriter json, GenericRecord record)
    {
        json.WriteStartObject();
        foreach (var field in record.Schema.Fields)
        {
            json.WritePropertyName(field.Name);
            record.TryGetValue(field.Name, out var value);
            WriteValue(json, value);
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case GenericRecord record:
                WriteRecord(json, record);
                break;
            case Guid guid:
                json.WriteStringValue(guid.ToString("D"));
                break;
            case DateTime time:
                json.WriteStringValue(DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                break;
            case AvroDecimal money:
                json.WriteNumberValue((decimal)money);
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case int number:
                json.WriteNumberValue(number);
                break;
            case long number:
                json.WriteNumberValue(number);
                break;
            case double number:
                json.WriteNumberValue(number);
                break;
            case float number:
                json.WriteNumberValue(number);
                break;
            case IEnumerable items:
                json.WriteStartArray();
                foreach (var item in items)
                {
                    WriteValue(json, item);
                }

                json.WriteEndArray();
                break;
            default:
                throw new NotSupportedException($"No JSON form for {value.GetType().Name}");
        }
    }
}

public static class AvroDecoderServiceCollectionExtensions
{
    /// <summary>
    /// Off unless <c>SchemaRegistry:Url</c> is set. <paramref name="readerSchemaAssembly"/> carries the consumer's own
    /// reader schemas as embedded resources, or is null for a consumer that wants whole records.
    /// </summary>
    public static IServiceCollection AddEventAvroDecoder(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        System.Reflection.Assembly? readerSchemaAssembly = null)
    {
        var url = configuration["SchemaRegistry:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            return services;
        }

        services.AddSingleton<IEventAvroDecoder>(sp => new AvroValueDecoder(
            new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = url }),
            readerSchemaAssembly is null ? new EventSchemaCatalog() : new EventSchemaCatalog(readerSchemaAssembly)));
        return services;
    }
}
