using System.Text.Json;
using DirectoryService.Application.Database;
using Shared.Outbox;

namespace DirectoryService.Infrastructure.Postgres;

public class OutboxWriter(DirectoryServiceDbContext dbContext) : IOutboxWriter
{
    public void Enqueue(string type, string aggregateId, object payload)
    {
        var message = OutboxMessage.Create(type, aggregateId, JsonSerializer.Serialize(payload));
        dbContext.Set<OutboxMessage>().Add(message);
    }
}