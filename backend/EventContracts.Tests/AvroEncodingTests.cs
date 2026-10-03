using System.Numerics;
using System.Reflection;
using Avro;
using Avro.Generic;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Avro;

namespace EventContracts.Tests;

// Every producer event, encoded by the code the outbox writers use and decoded by the real Confluent deserializer
// against a real registry: the bytes on the wire are what a consumer will read. Optional fields are exercised both
// empty and filled.
public sealed class RegistryFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("quay.io/apicurio/apicurio-registry:3.0.7")
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/apis/registry/v3/system/info").ForPort(8080)))
        .Build();

    public string Url => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}/apis/ccompat/v7";

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public class AvroEncodingTests(RegistryFixture registry) : IClassFixture<RegistryFixture>
{
    internal static readonly Assembly[] Producers =
    [
        typeof(EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent).Assembly,
        typeof(AuthService.Application.IntegrationEvents.AccountProvisionedEvent).Assembly,
        typeof(DirectoryService.Application.IntegrationEvents.DepartmentCreatedEvent).Assembly,
        typeof(RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent).Assembly,
        typeof(Shared.IntegrationEvents.OutboxMessageRedrivenEvent).Assembly,
    ];

    private static readonly NullabilityInfoContext Nullability = new();

    internal static IEnumerable<Type> EventTypes() => Producers
        .SelectMany(a => a.GetExportedTypes())
        .Where(t => t.Name.EndsWith("Event", StringComparison.Ordinal)
            && t.Namespace is { } ns && ns.EndsWith(".IntegrationEvents", StringComparison.Ordinal)
            && t.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
        .OrderBy(t => t.Name);

    public static IEnumerable<object[]> Events() => EventTypes().SelectMany(t => new[] { new object[] { t, false }, new object[] { t, true } });

    private async Task<(EventAvroEncoder Encoder, CachedSchemaRegistryClient Client)> Warmed(Assembly assembly, string topic)
    {
        var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registry.Url });
        var encoder = new EventAvroEncoder(new EventSchemaCatalog(assembly), client, topic, NullLogger<EventAvroEncoder>.Instance);
        await encoder.WarmUpAsync(CancellationToken.None);
        return (encoder, client);
    }

    [Theory]
    [MemberData(nameof(Events))]
    public async Task An_event_encodes_to_the_wire_format_and_decodes_back_to_what_was_sent(Type eventType, bool fillOptionals)
    {
        var (encoder, client) = await Warmed(eventType.Assembly, "contracts-test.events.v2");
        var payload = Sample(eventType, fillOptionals);
        var name = eventType.Name[..^"Event".Length];

        Assert.True(encoder.TryEncode(name, payload, out var bytes));

        Assert.Equal(0, bytes[0]);
        var deserializer = new AvroDeserializer<GenericRecord>(client);
        var record = await deserializer.DeserializeAsync(bytes, false, new SerializationContext(MessageComponentType.Value, "contracts-test.events.v2"));
        foreach (var property in eventType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            record.TryGetValue(property.Name, out var actual);
            Assert.Equal(Normalize(property.GetValue(payload)), NormalizeDecoded(actual));
        }
    }

    [Theory]
    [MemberData(nameof(Events))]
    public async Task The_bytes_made_from_the_stored_json_are_identical_to_the_bytes_made_from_the_live_event(Type eventType, bool fillOptionals)
    {
        // A row written while the registry was down is encoded at publish time from its JSON; a consumer must not be able
        // to tell which path made it.
        var (encoder, _) = await Warmed(eventType.Assembly, "contracts-test.json.v2");
        var payload = Sample(eventType, fillOptionals);
        var name = eventType.Name[..^"Event".Length];

        Assert.True(encoder.TryEncode(name, payload, out var live));
        var fromJson = await encoder.EncodeJsonAsync(name, System.Text.Json.JsonSerializer.Serialize(payload, eventType), CancellationToken.None);

        Assert.Equal(live, fromJson);
    }

    [Fact]
    public void Every_producer_assembly_carries_the_schemas_of_its_events_as_embedded_resources()
    {
        // A schema that is in git but not compiled in would never be registered by the service that publishes it.
        foreach (var assembly in Producers)
        {
            var expected = EventTypes().Where(t => t.Assembly == assembly).Select(t => t.Name[..^"Event".Length]).Order();
            // The shared assembly's schema is in every service's catalog too, so its own is the whole of what it carries.
            var embedded = assembly.GetManifestResourceNames()
                .Where(n => n.Contains(".IntegrationEvents.Schemas.", StringComparison.Ordinal))
                .Select(n => n.Split('.')[^2])
                .Order();
            Assert.Equal(expected, embedded);
        }
    }

    [Fact]
    public async Task Nothing_is_encoded_until_the_schemas_are_registered()
    {
        var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registry.Url });
        var encoder = new EventAvroEncoder(
            new EventSchemaCatalog(typeof(EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent).Assembly),
            client, "contracts-test.cold.v2", NullLogger<EventAvroEncoder>.Instance);
        var hired = new EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent(Guid.NewGuid(), "A", "a@b.c", Guid.NewGuid(), Guid.NewGuid());

        Assert.False(encoder.IsReady);
        Assert.False(encoder.TryEncode("EmployeeHired", hired, out _));

        await encoder.WarmUpAsync(CancellationToken.None);

        Assert.True(encoder.IsReady);
        Assert.True(encoder.TryEncode("EmployeeHired", hired, out _));
    }

    [Fact]
    public async Task The_schema_id_in_the_bytes_is_the_one_registered_under_the_topic_and_record_name()
    {
        var (encoder, client) = await Warmed(typeof(EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent).Assembly, "contracts-test.id.v2");
        var hired = new EmployeeService.Application.IntegrationEvents.EmployeeHiredEvent(Guid.NewGuid(), "A", "a@b.c", Guid.NewGuid(), Guid.NewGuid());

        Assert.True(encoder.TryEncode("EmployeeHired", hired, out var bytes));

        var registered = await client.GetLatestSchemaAsync("contracts-test.id.v2-ds.employee.EmployeeHired");
        Assert.Equal(registered.Id, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)));
    }

    [Fact]
    public async Task Money_with_more_than_two_decimals_is_refused_not_rounded()
    {
        var (encoder, _) = await Warmed(typeof(RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent).Assembly, "contracts-test.money.v2");
        var grant = new RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent(Guid.NewGuid(), 1.234m, "bonus", 5m);

        Assert.Throws<InvalidOperationException>(() => encoder.TryEncode("CurrencyGranted", grant, out _));
    }

    [Theory]
    [InlineData("1234.5", "1234.5")]
    [InlineData("1234.50", "1234.5")]
    [InlineData("-5", "-5")]
    [InlineData("0", "0")]
    [InlineData("0.01", "0.01")]
    public async Task Money_comes_back_exactly_whatever_scale_it_was_written_with(string sent, string expected)
    {
        var (encoder, client) = await Warmed(typeof(RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent).Assembly, "contracts-test.money2.v2");
        var grant = new RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent(Guid.NewGuid(), decimal.Parse(sent, System.Globalization.CultureInfo.InvariantCulture), "bonus", 5m);

        Assert.True(encoder.TryEncode("CurrencyGranted", grant, out var bytes));

        var record = await new AvroDeserializer<GenericRecord>(client).DeserializeAsync(bytes, false, new SerializationContext(MessageComponentType.Value, "contracts-test.money2.v2"));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), NormalizeDecoded(record["Amount"]));
    }

    internal static object Sample(Type eventType, bool fillOptionals)
    {
        var constructor = eventType.GetConstructors().Single();
        var args = constructor.GetParameters().Select(p => SampleValue(p.ParameterType, p.Name!, IsOptional(p), fillOptionals)).ToArray();
        return constructor.Invoke(args);
    }

    private static bool IsOptional(ParameterInfo parameter) =>
        Nullable.GetUnderlyingType(parameter.ParameterType) is not null
        || (!parameter.ParameterType.IsValueType && Nullability.Create(parameter).WriteState == NullabilityState.Nullable);

    private static object? SampleValue(Type type, string name, bool optional, bool fill)
    {
        if (optional && !fill)
        {
            return null;
        }

        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (t == typeof(string))
        {
            return name + "-value";
        }

        if (t == typeof(DateTime))
        {
            return new DateTime(2026, 10, 2, 8, 30, 15, 123, DateTimeKind.Utc);
        }

        if (t == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 10, 2, 8, 30, 15, 123, TimeSpan.FromHours(3));
        }

        if (t == typeof(decimal))
        {
            return 1234.56m;
        }

        if (t == typeof(bool))
        {
            return true;
        }

        if (t == typeof(Guid[]))
        {
            return new[] { Guid.NewGuid(), Guid.NewGuid() };
        }

        if (t == typeof(IReadOnlyList<string>))
        {
            return new List<string> { "Admin", "Editor" };
        }

        throw new NotSupportedException($"No sample for {t} ({name})");
    }

    // What the producer meant, in a form comparable with what comes back off the wire.
    internal static object? Normalize(object? value) => value switch
    {
        null => null,
        Guid g => g.ToString("D"),
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        DateTimeOffset o => o.ToUnixTimeMilliseconds(),
        Guid[] gs => gs.Select(g => g.ToString("D")).ToList<object?>(),
        IReadOnlyList<string> ss => ss.Cast<object?>().ToList(),
        _ => value,
    };

    private static object? NormalizeDecoded(object? value) => value switch
    {
        null => null,
        Guid g => g.ToString("D"),
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        AvroDecimal ad => (decimal)ad,
        byte[] b => (decimal)new BigInteger(b, isUnsigned: false, isBigEndian: true) / 100m,
        object[] items => items.Select(NormalizeDecoded).ToList(),
        System.Collections.IEnumerable e when value is not string => e.Cast<object?>().Select(NormalizeDecoded).ToList(),
        _ => value,
    };
}
