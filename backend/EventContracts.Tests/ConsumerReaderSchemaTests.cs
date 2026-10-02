using System.Reflection;
using System.Text.Json;
using Avro;
using Confluent.SchemaRegistry;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Avro;

namespace EventContracts.Tests;

// A consumer's local copy of an event and its reader schema are two descriptions of one expectation, and the registry
// only checks the second. These keep them the same, then run the real thing: the producer's bytes, decoded with the
// consumer's own reader schema, deserialized into the consumer's own record.
public class ConsumerReaderSchemaTests(RegistryFixture registry) : IClassFixture<RegistryFixture>
{
    private static readonly Assembly[] Consumers =
    [
        typeof(AuthService.Web.Consumers.EmployeeHiredEvent).Assembly,
        typeof(EmployeeService.Web.Consumers.AccountProvisionedEvent).Assembly,
        typeof(NotificationService.Web.Consumers.CurrencyGrantedEvent).Assembly,
        typeof(SearchService.Web.Consumers.EmployeeHiredEvent).Assembly,
        typeof(RewardsService.Infrastructure.Consumers.EmployeeHiredEvent).Assembly,
    ];

    private static readonly NullabilityInfoContext Nullability = new();

    public static IEnumerable<object[]> Records() => Consumers
        .SelectMany(a => a.GetExportedTypes())
        .Where(t => t.Name.EndsWith("Event", StringComparison.Ordinal)
            && t.Namespace is { } ns && ns.EndsWith(".Consumers", StringComparison.Ordinal)
            && t.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null)
        .OrderBy(t => t.FullName)
        .Select(t => new object[] { t });

    private static RecordSchema ReaderOf(Type record)
    {
        var name = record.Name[..^"Event".Length];
        Assert.True(
            new EventSchemaCatalog(record.Assembly).TryGet(name, out var schema, out _),
            $"{record.FullName} has no reader schema {name}.avsc in {record.Assembly.GetName().Name}/Consumers/Schemas");
        return schema;
    }

    [Fact]
    public void The_discovery_finds_every_consumer_record()
    {
        Assert.True(Records().Count() >= 17);
    }

    [Theory]
    [MemberData(nameof(Records))]
    public void The_reader_schema_declares_exactly_the_fields_of_the_record_with_the_same_optionality(Type record)
    {
        var schema = ReaderOf(record);
        var properties = record.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).ToList();

        Assert.Equal(properties.Select(p => p.Name).Order(StringComparer.Ordinal), schema.Fields.Select(f => f.Name).Order(StringComparer.Ordinal));
        foreach (var property in properties)
        {
            var optionalInCode = Nullable.GetUnderlyingType(property.PropertyType) is not null
                || (!property.PropertyType.IsValueType && Nullability.Create(property).ReadState == NullabilityState.Nullable);
            var field = schema.Fields.Single(f => f.Name == property.Name);
            var nullable = field.Schema is UnionSchema u && u.Schemas.Any(s => s.Tag == Avro.Schema.Type.Null);
            Assert.True(optionalInCode == nullable, $"{record.Name}.{property.Name}: nullable in code = {optionalInCode}, in the reader schema = {nullable}");
        }
    }

    [Fact]
    public void Every_reader_schema_file_belongs_to_a_consumer_record()
    {
        var known = Records().Select(r => ((Type)r[0]).Name[..^"Event".Length]).ToHashSet();
        foreach (var assembly in Consumers)
        {
            var catalog = new EventSchemaCatalog(assembly);
            var readers = assembly.GetManifestResourceNames().Where(n => n.Contains(".Consumers.Schemas.", StringComparison.Ordinal)).Select(n => n.Split('.')[^2]);
            Assert.All(readers, name => Assert.Contains(name, known));
            _ = catalog;
        }
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task What_the_producer_wrote_reaches_the_consumers_own_record_through_its_own_reader_schema(Type record)
    {
        var name = record.Name[..^"Event".Length];
        var producerType = AvroEncodingTests.Events().Select(e => (Type)e[0]).Distinct().Single(t => t.Name == record.Name);
        var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = registry.Url });
        var encoder = new EventAvroEncoder(new EventSchemaCatalog(producerType.Assembly), client, "reader-test.events.v2", NullLogger<EventAvroEncoder>.Instance);
        await encoder.WarmUpAsync(CancellationToken.None);
        var decoder = new AvroValueDecoder(client, new EventSchemaCatalog(record.Assembly));

        foreach (var fill in new[] { false, true })
        {
            var sent = AvroEncodingTests.Sample(producerType, fill);
            Assert.True(encoder.TryEncode(name, sent, out var bytes));

            var json = decoder.Decode(bytes, name).Json;
            var received = JsonSerializer.Deserialize(json, record)!;

            foreach (var property in record.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                Assert.Equal(
                    AvroEncodingTests.Normalize(producerType.GetProperty(property.Name)!.GetValue(sent)),
                    AvroEncodingTests.Normalize(property.GetValue(received)));
            }

            // Only what the consumer declared comes through; nothing else of the producer's reaches it.
            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                record.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(p => p.Name).Order(StringComparer.Ordinal),
                document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        }
    }
}
