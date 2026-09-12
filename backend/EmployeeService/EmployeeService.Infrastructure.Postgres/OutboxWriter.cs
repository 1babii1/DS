using System.Text.Json;
using EmployeeService.Application.Database;
using Shared.Outbox;

namespace EmployeeService.Infrastructure.Postgres;

public class OutboxWriter(EmployeeDbContext dbContext) : IOutboxWriter
{
    public void Enqueue(string type, string aggregateId, object payload)
    {
        var message = OutboxMessage.Create(type, aggregateId, JsonSerializer.Serialize(payload));
        dbContext.Set<OutboxMessage>().Add(message);
    }
}
