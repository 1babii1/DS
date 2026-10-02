using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Avro.Generic;
using Avro.IO;
using Confluent.SchemaRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shared.Avro;

/// <summary>
/// Encodes an event into the Confluent wire format (a zero byte, the schema id, the Avro binary body) so that what the
/// outbox stores is exactly what a consumer, or a CDC connector, will carry. Synchronous on purpose: it runs where the
/// outbox row is staged, inside the domain transaction, so it can only use schema ids that are already known. When one is
/// not (the registry was unreachable so far) it says so and the caller keeps the JSON payload, which a later step encodes
/// at publish time.
/// </summary>
public interface IEventAvroEncoder
{
    bool IsReady { get; }

    bool TryEncode(string eventType, object payload, out byte[] bytes);
}

/// <summary>What a service without a registry configured gets: nothing is ever encoded.</summary>
public sealed class NullEventAvroEncoder : IEventAvroEncoder
{
    public bool IsReady => false;

    public bool TryEncode(string eventType, object payload, out byte[] bytes)
    {
        bytes = [];
        return false;
    }
}

public sealed class EventAvroEncoder(EventSchemaCatalog catalog, ISchemaRegistryClient registry, string topic, ILogger<EventAvroEncoder> logger)
    : IEventAvroEncoder
{
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.Ordinal);

    public bool IsReady => catalog.EventTypes.All(_ids.ContainsKey);

    /// <summary>The subject an event's schema is registered under: <c>&lt;topic&gt;-&lt;namespace&gt;.&lt;Record&gt;</c>.</summary>
    public string SubjectOf(string eventType)
    {
        catalog.TryGet(eventType, out var schema, out _);
        return $"{topic}-{schema.Fullname}";
    }

    /// <summary>Registers every schema (idempotent) and remembers the ids. Throws while the registry is unreachable.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        foreach (var eventType in catalog.EventTypes)
        {
            if (_ids.ContainsKey(eventType))
            {
                continue;
            }

            catalog.TryGet(eventType, out _, out var json);
            var id = await registry.RegisterSchemaAsync(SubjectOf(eventType), new Confluent.SchemaRegistry.Schema(json, SchemaType.Avro));
            _ids[eventType] = id;
            logger.LogDebug("Schema for {EventType} is id {SchemaId} under {Subject}", eventType, id, SubjectOf(eventType));
        }
    }

    public bool TryEncode(string eventType, object payload, out byte[] bytes)
    {
        bytes = [];
        if (!_ids.TryGetValue(eventType, out var id) || !catalog.TryGet(eventType, out var schema, out _))
        {
            return false;
        }

        var record = AvroRecordMapper.ToGenericRecord(schema, payload);
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, id);
        stream.Write(header);
        var encoder = new BinaryEncoder(stream);
        new GenericDatumWriter<GenericRecord>(schema).Write(record, encoder);
        encoder.Flush();
        bytes = stream.ToArray();
        return true;
    }
}

/// <summary>Keeps trying to register the schemas until the registry answers; never stops the host.</summary>
public sealed class SchemaWarmUpService(EventAvroEncoder encoder, ILogger<SchemaWarmUpService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await encoder.WarmUpAsync(stoppingToken);
                logger.LogInformation("Event schemas are registered; outbox rows will carry Avro");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Schema registry not ready; outbox rows keep only JSON for now. Retrying in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }
}

public static class AvroServiceCollectionExtensions
{
    /// <summary>
    /// Off unless <c>SchemaRegistry:Url</c> is set, so a service started without a registry behaves as it always did.
    /// <paramref name="topic"/> is the Avro topic the events will go to (its name is part of the registry subject).
    /// </summary>
    public static IServiceCollection AddEventAvroEncoder(
        this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration, string topic, Assembly schemaAssembly)
    {
        var url = configuration["SchemaRegistry:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            services.AddSingleton<IEventAvroEncoder, NullEventAvroEncoder>();
            return services;
        }

        services.AddSingleton(new EventSchemaCatalog(schemaAssembly));
        services.AddSingleton<ISchemaRegistryClient>(_ => new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = url }));
        services.AddSingleton(sp => new EventAvroEncoder(
            sp.GetRequiredService<EventSchemaCatalog>(),
            sp.GetRequiredService<ISchemaRegistryClient>(),
            topic,
            sp.GetRequiredService<ILogger<EventAvroEncoder>>()));
        services.AddSingleton<IEventAvroEncoder>(sp => sp.GetRequiredService<EventAvroEncoder>());
        services.AddHostedService<SchemaWarmUpService>();
        return services;
    }
}
