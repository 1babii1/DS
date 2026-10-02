using System.Text.Json;
using EmployeeService.Application.Database;
using Shared.Avro;
using Shared.Outbox;

namespace EmployeeService.Infrastructure.Postgres;

public class OutboxWriter(EmployeeDbContext dbContext, IEventAvroEncoder? avro = null) : IOutboxWriter
{
    public void Enqueue(string type, string aggregateId, object payload)
    {
        var message = OutboxMessage.Create(type, aggregateId, JsonSerializer.Serialize(payload));
        message.StageAvro(avro, type, payload);

        dbContext.Set<OutboxMessage>().Add(message);
    }
}