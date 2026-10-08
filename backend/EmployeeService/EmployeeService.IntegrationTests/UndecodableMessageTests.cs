using System.Text.Json;
using Confluent.Kafka;
using EmployeeService.Infrastructure.Postgres;
using EmployeeService.Web.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Avro;
using Shared.Kafka;

namespace EmployeeService.IntegrationTests;

// An Avro message that can never be decoded is parked in the dead letters with its bytes, so that it does not stall the consumer (KafkaRetryConsumer).
// The decision to park it was tested; that the parking itself works against the real table was not, and it did not: the dead letters keep the payload as
// json, and the value kept for such a message was not json, so the write failed and the consumer asked for the same message again, for ever. Found by
// pointing a new consumer at a topic with history (ADR 0057).
public class UndecodableMessageTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public UndecodableMessageTests(EmployeeTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    [Fact]
    public async Task A_message_that_cannot_be_decoded_is_parked_with_its_bytes_and_the_consumer_moves_on()
    {
        var consumer = new AuthEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmployeeConsumerOptions()),
            _services.GetRequiredService<ILogger<AuthEventsConsumer>>(),
            new RefusingDecoder());
        var id = Guid.NewGuid();
        byte[] bytes = [0, 0, 0, 0, 7, 1, 2, 3];

        var text = consumer.AsText(Raw(id, bytes), out var undecodable);
        Assert.NotNull(undecodable);

        Assert.True(consumer.TryDeadLetter(text, undecodable!), "the message could not be parked, so the consumer would ask for it again for ever");

        await using var scope = _services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<EmployeeDbContext>().DeadLetters.AsNoTracking().SingleAsync(d => d.MessageId == id);
        // Valid json, and the bytes can be had back from it.
        var kept = JsonDocument.Parse(entry.Payload).RootElement.GetString();
        Assert.Equal("avro-undecodable:" + Convert.ToBase64String(bytes), kept);
    }

    private static ConsumeResult<string, byte[]> Raw(Guid messageId, byte[] value) => new()
    {
        Topic = "directory.events.v2",
        Message = new Message<string, byte[]>
        {
            Key = "k",
            Value = value,
            Headers = new Headers
            {
                { "message-id", System.Text.Encoding.UTF8.GetBytes(messageId.ToString()) },
                { "message-type", System.Text.Encoding.UTF8.GetBytes("DepartmentCreated") },
            },
        },
    };

    private sealed class RefusingDecoder : IEventAvroDecoder
    {
        public DecodedEvent Decode(byte[] bytes, string? eventType) => throw new FormatException("the bytes are not what their schema says");
    }
}
