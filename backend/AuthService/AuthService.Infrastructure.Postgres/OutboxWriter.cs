using System.Text.Json;
using AuthService.Application.Database;
using Shared.Outbox;

namespace AuthService.Infrastructure.Postgres;

public class OutboxWriter(AuthDbContext dbContext) : IOutboxWriter
{
    public void Enqueue(string type, string aggregateId, object payload)
    {
        var message = OutboxMessage.Create(type, aggregateId, JsonSerializer.Serialize(payload));
        dbContext.Set<OutboxMessage>().Add(message);
    }
}
