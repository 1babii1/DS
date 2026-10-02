using System.Reflection;
using System.Text.Json;
using Confluent.SchemaRegistry;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Avro;

namespace EventContracts.Tests;

// The other half of the wire: what a consumer gets back. Every event is encoded by the producer's code and decoded by the
// consumer's, and the JSON that results must deserialize, with the System.Text.Json defaults the consumers use, to what the
// producer meant. Reader schemas are exercised with Avro's own resolution.
public class AvroDecodingTests(RegistryFixture registry) : IClassFixture<RegistryFixture>
{
    private CachedSchemaRegistryClient Client() => new(new SchemaRegistryConfig { Url = registry.Url });

    private async Task<(EventAvroEncoder Encoder, AvroValueDecoder Decoder)> Pair(Assembly producer, string topic, EventSchemaCatalog? readers = null)
    {
        var client = Client();
        var encoder = new EventAvroEncoder(new EventSchemaCatalog(producer), client, topic, NullLogger<EventAvroEncoder>.Instance);
        await encoder.WarmUpAsync(CancellationToken.None);
        return (encoder, new AvroValueDecoder(client, readers ?? new EventSchemaCatalog()));
    }

    [Theory]
    [MemberData(nameof(AvroEncodingTests.Events), MemberType = typeof(AvroEncodingTests))]
    public async Task What_a_consumer_decodes_is_what_the_producer_sent(Type eventType, bool fillOptionals)
    {
        var (encoder, decoder) = await Pair(eventType.Assembly, "decode-test.events.v2");
        var sent = AvroEncodingTests.Sample(eventType, fillOptionals);
        var name = eventType.Name[..^"Event".Length];
        Assert.True(encoder.TryEncode(name, sent, out var bytes));

        var decoded = decoder.Decode(bytes, name);

        var received = JsonSerializer.Deserialize(decoded.Json, eventType)!;
        foreach (var property in eventType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            Assert.Equal(
                AvroEncodingTests.Normalize(property.GetValue(sent)),
                AvroEncodingTests.Normalize(property.GetValue(received)));
        }
    }

    private const string Writer = """{"type":"record","name":"Pet","namespace":"ds.test","fields":[{"name":"Id","type":{"type":"string","logicalType":"uuid"}},{"name":"Name","type":"string"},{"name":"Tag","type":["null","string"],"default":null}]}""";

    private async Task<(AvroValueDecoder Decoder, byte[] Bytes)> PetWritten(string reader)
    {
        var client = Client();
        var catalog = EventSchemaCatalog.FromJson(Writer);
        var encoder = new EventAvroEncoder(catalog, client, "decode-test.pets.v2", NullLogger<EventAvroEncoder>.Instance);
        await encoder.WarmUpAsync(CancellationToken.None);
        Assert.True(encoder.TryEncode("Pet", new PetRecord(Guid.NewGuid(), "Rex", "good"), out var bytes));
        return (new AvroValueDecoder(client, EventSchemaCatalog.FromJson(reader)), bytes);
    }

    private sealed record PetRecord(Guid Id, string Name, string? Tag);

    [Fact]
    public async Task A_reader_schema_drops_the_fields_it_did_not_declare_and_defaults_the_ones_the_writer_lacks()
    {
        const string reader = """{"type":"record","name":"Pet","namespace":"ds.test","fields":[{"name":"Name","type":"string"},{"name":"Nickname","type":["null","string"],"default":null},{"name":"Legs","type":"int","default":4}]}""";
        var (decoder, bytes) = await PetWritten(reader);

        using var json = JsonDocument.Parse(decoder.Decode(bytes, "Pet").Json);

        Assert.Equal(["Name", "Nickname", "Legs"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Rex", json.RootElement.GetProperty("Name").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Nickname").ValueKind);
        Assert.Equal(4, json.RootElement.GetProperty("Legs").GetInt32());
    }

    [Fact]
    public async Task Without_a_reader_schema_the_writers_whole_record_comes_back()
    {
        var (decoder, bytes) = await PetWritten(Writer.Replace("\"Pet\"", "\"Other\""));

        using var json = JsonDocument.Parse(decoder.Decode(bytes, "Pet").Json);

        Assert.Equal(["Id", "Name", "Tag"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_schema_id_the_event_was_written_with_is_reported()
    {
        var (decoder, bytes) = await PetWritten(Writer);

        var decoded = decoder.Decode(bytes, "Pet");

        Assert.Equal(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(1, 4)), decoded.SchemaId);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 0, 1, 2 })]
    public void Bytes_that_are_not_an_avro_message_are_refused_as_malformed(byte[] bytes)
    {
        var decoder = new AvroValueDecoder(Client(), new EventSchemaCatalog());

        Assert.Throws<FormatException>(() => decoder.Decode(bytes, null));
    }
}
