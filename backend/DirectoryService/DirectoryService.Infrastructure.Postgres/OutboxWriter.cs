using System.Text.Json;
using DirectoryService.Application.Database;
using Shared.Avro;
using Shared.Outbox;

namespace DirectoryService.Infrastructure.Postgres;

public class OutboxWriter(DirectoryServiceDbContext dbContext, IEventAvroEncoder? avro = null) : IOutboxWriter
{
    public void Enqueue(string type, string aggregateId, object payload)
    {
        var message = OutboxMessage.Create(type, aggregateId, JsonSerializer.Serialize(payload));
        if (avro is not null && avro.TryEncode(type, payload, out var bytes))
        {
            message.AttachAvro(bytes);
        }

        dbContext.Set<OutboxMessage>().Add(message);
    }
}