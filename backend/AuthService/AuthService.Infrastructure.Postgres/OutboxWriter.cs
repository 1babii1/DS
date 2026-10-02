using System.Text.Json;
using AuthService.Application.Database;
using Shared.Avro;
using Shared.Outbox;

namespace AuthService.Infrastructure.Postgres;

public class OutboxWriter(AuthDbContext dbContext, IEventAvroEncoder? avro = null) : IOutboxWriter
{
    public void Enqueue(string type, string aggregateId, object payload)
    {
        var message = OutboxMessage.Create(type, aggregateId, JsonSerializer.Serialize(payload));
        if (avro is { IsConfigured: true })
        {
            message.ExpectAvro(avro.TryEncode(type, payload, out var bytes) ? bytes : null);
        }

        dbContext.Set<OutboxMessage>().Add(message);
    }
}
