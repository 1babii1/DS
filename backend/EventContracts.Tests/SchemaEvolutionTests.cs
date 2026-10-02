using System.Text.Json;
using Confluent.SchemaRegistry;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Avro;

namespace EventContracts.Tests;

// The reason for all of it (ADR 0023): an event written under any earlier version of its schema can be read by today's
// code, and an event written under today's schema can be read by a consumer that has not been redeployed. Run through the
// real registry and the real encoder and decoder, with the evolution the runbook allows (add an optional field) and the
// one it forbids (add a required field).
public class SchemaEvolutionTests(RegistryFixture registry) : IClassFixture<RegistryFixture>
{
    private const string V1 = """{"type":"record","name":"Hired","namespace":"ds.evo","fields":[{"name":"Id","type":{"type":"string","logicalType":"uuid"}},{"name":"Name","type":"string"}]}""";
    private const string V2 = """{"type":"record","name":"Hired","namespace":"ds.evo","fields":[{"name":"Id","type":{"type":"string","logicalType":"uuid"}},{"name":"Name","type":"string"},{"name":"Grade","type":["null","int"],"default":null}]}""";
    private const string V3 = """{"type":"record","name":"Hired","namespace":"ds.evo","fields":[{"name":"Id","type":{"type":"string","logicalType":"uuid"}},{"name":"Name","type":"string"},{"name":"Grade","type":["null","int"],"default":null},{"name":"Team","type":["null","string"],"default":null,"doc":"Added in v3"}]}""";

    private sealed record Hired(Guid Id, string Name);

    private sealed record HiredV2(Guid Id, string Name, int? Grade);

    private sealed record HiredV3(Guid Id, string Name, int? Grade, string? Team);

    private CachedSchemaRegistryClient Client() => new(new SchemaRegistryConfig { Url = registry.Url });

    private static async Task<byte[]> Write(CachedSchemaRegistryClient client, string topic, string schema, object record)
    {
        var encoder = new EventAvroEncoder(EventSchemaCatalog.FromJson(schema), client, topic, NullLogger<EventAvroEncoder>.Instance);
        await encoder.WarmUpAsync(CancellationToken.None);
        Assert.True(encoder.TryEncode("Hired", record, out var bytes));
        return bytes;
    }

    private static JsonElement Read(AvroValueDecoder decoder, byte[] bytes)
    {
        using var document = JsonDocument.Parse(decoder.Decode(bytes, "Hired").Json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task An_event_written_years_ago_is_read_by_todays_schema_and_the_new_field_takes_its_default()
    {
        var client = Client();
        var old = await Write(client, "evo-a.events.v2", V1, new Hired(Guid.NewGuid(), "Maria"));
        // Later versions registered after the old event was written.
        await Write(client, "evo-a.events.v2", V2, new HiredV2(Guid.NewGuid(), "x", 1));
        await Write(client, "evo-a.events.v2", V3, new HiredV3(Guid.NewGuid(), "x", 1, "Core"));

        var today = Read(new AvroValueDecoder(client, EventSchemaCatalog.FromJson(V3)), old);

        Assert.Equal("Maria", today.GetProperty("Name").GetString());
        Assert.Equal(JsonValueKind.Null, today.GetProperty("Grade").ValueKind);
        Assert.Equal(JsonValueKind.Null, today.GetProperty("Team").ValueKind);
    }

    [Fact]
    public async Task An_event_written_today_is_read_by_a_consumer_that_was_not_redeployed()
    {
        var client = Client();
        var fresh = await Write(client, "evo-b.events.v2", V1, new Hired(Guid.NewGuid(), "seed"));
        var newest = await Write(client, "evo-b.events.v2", V3, new HiredV3(Guid.NewGuid(), "Ivan", 7, "Core"));
        _ = fresh;

        var asV1Consumer = Read(new AvroValueDecoder(client, EventSchemaCatalog.FromJson(V1)), newest);

        Assert.Equal(["Id", "Name"], asV1Consumer.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Ivan", asV1Consumer.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Every_version_in_the_history_is_read_by_the_latest_reader()
    {
        var client = Client();
        var bytes = new[]
        {
            await Write(client, "evo-c.events.v2", V1, new Hired(Guid.NewGuid(), "one")),
            await Write(client, "evo-c.events.v2", V2, new HiredV2(Guid.NewGuid(), "two", 2)),
            await Write(client, "evo-c.events.v2", V3, new HiredV3(Guid.NewGuid(), "three", 3, "T")),
        };
        var latest = new AvroValueDecoder(client, EventSchemaCatalog.FromJson(V3));

        var names = bytes.Select(b => Read(latest, b).GetProperty("Name").GetString()).ToList();

        Assert.Equal(["one", "two", "three"], names);
    }

    [Fact]
    public async Task A_required_field_added_to_an_event_is_refused_by_the_registry_so_old_events_stay_readable()
    {
        var client = Client();
        await Write(client, "evo-d.events.v2", V1, new Hired(Guid.NewGuid(), "seed"));
        const string breaking = """{"type":"record","name":"Hired","namespace":"ds.evo","fields":[{"name":"Id","type":{"type":"string","logicalType":"uuid"}},{"name":"Name","type":"string"},{"name":"Grade","type":"int"}]}""";

        var refused = await Assert.ThrowsAsync<SchemaRegistryException>(
            () => client.RegisterSchemaAsync("evo-d.events.v2-ds.evo.Hired", new Schema(breaking, SchemaType.Avro)));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.Status);
    }
}
