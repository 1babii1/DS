using System.Text;
using AuditService.Infrastructure;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Avro;

namespace AuditService.IntegrationTests;

// ADR 0023: the audit log keeps the event as JSON whichever topic it came from, remembers which schema wrote it, and does
// not call "directory.events.v2" a different source from "directory.events".
public class AuditAvroTests : IClassFixture<AuditTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public AuditAvroTests(AuditTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    private sealed class StubDecoder(string json, int schemaId, Exception? fail = null) : IEventAvroDecoder
    {
        public DecodedEvent Decode(byte[] bytes, string? eventType) => fail is not null ? throw fail : new DecodedEvent(json, schemaId);
    }

    private AuditConsumer Consumer(IEventAvroDecoder? decoder) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new AuditConsumerOptions()),
        _services.GetRequiredService<ILogger<AuditConsumer>>(),
        decoder);

    private static ConsumeResult<string, byte[]> Raw(Guid id, string topic, string type, byte[] value) => new()
    {
        Topic = topic,
        Message = new Message<string, byte[]>
        {
            Key = "agg-1",
            Value = value,
            Headers = new Headers
            {
                { "message-id", Encoding.UTF8.GetBytes(id.ToString()) },
                { "message-type", Encoding.UTF8.GetBytes(type) },
            },
        },
    };

    [Fact]
    public async Task An_avro_message_is_stored_as_the_decoded_json_with_its_schema_id_under_the_plain_source_name()
    {
        var id = Guid.NewGuid();
        const string json = """{"DepartmentId":"3f2c1c1e-0000-4000-8000-000000000001","Name":"Payments"}""";
        var sut = Consumer(new StubDecoder(json, 42));

        var text = sut.AsText(Raw(id, "directory.events.v2", "DepartmentRenamed", [0, 0, 0, 0, 42, 1, 2]), out var undecodable);
        Assert.Null(undecodable);
        Assert.True(sut.HandleWithRetryAndDeadLetter(text, CancellationToken.None));

        var entry = await OnlyEntry(id);
        // Stored in a jsonb column, which normalizes spacing and key order: the same document, not the same text.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(json), System.Text.Json.Nodes.JsonNode.Parse(entry.Payload)));
        Assert.Equal(42, entry.SchemaId);
        Assert.Equal("directory", entry.SourceService);
        Assert.Equal("DepartmentRenamed", entry.EventType);
    }

    [Fact]
    public async Task A_json_message_is_stored_as_it_always_was_with_no_schema_id()
    {
        var id = Guid.NewGuid();
        var sut = Consumer(decoder: null);

        var text = sut.AsText(Raw(id, "employee.events", "EmployeeHired", Encoding.UTF8.GetBytes("""{"EmployeeId":"x"}""")), out var undecodable);
        Assert.Null(undecodable);
        Assert.True(sut.HandleWithRetryAndDeadLetter(text, CancellationToken.None));

        var entry = await OnlyEntry(id);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse("""{"EmployeeId":"x"}"""), System.Text.Json.Nodes.JsonNode.Parse(entry.Payload)));
        Assert.Null(entry.SchemaId);
        Assert.Equal("employee", entry.SourceService);
    }

    [Fact]
    public void An_avro_message_that_cannot_be_decoded_is_set_aside_with_its_bytes_not_stored_as_an_entry()
    {
        var sut = Consumer(new StubDecoder("{}", 1, new FormatException("corrupt")));

        var text = sut.AsText(Raw(Guid.NewGuid(), "employee.events.v2", "EmployeeHired", [0, 9, 9]), out var undecodable);

        Assert.IsType<FormatException>(undecodable);
        Assert.Equal("avro-undecodable:" + Convert.ToBase64String(new byte[] { 0, 9, 9 }), text.Message.Value);
    }

    [Fact]
    public void An_avro_message_with_no_decoder_configured_is_an_error_to_wait_out_not_poison()
    {
        var sut = Consumer(decoder: null);

        Assert.Throws<InvalidOperationException>(
            () => sut.AsText(Raw(Guid.NewGuid(), "employee.events.v2", "EmployeeHired", [0, 0, 0, 0, 1]), out _));
    }

    private async Task<AuditService.Domain.AuditEntry> OnlyEntry(Guid messageId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Entries.AsNoTracking().SingleAsync(e => e.MessageId == messageId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();
}
