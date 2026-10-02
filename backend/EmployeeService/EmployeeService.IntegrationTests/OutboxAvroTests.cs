using EmployeeService.Application.IntegrationEvents;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Avro;
using Shared.Outbox;

namespace EmployeeService.IntegrationTests;

// ADR 0023: the hire event carries Avro next to its JSON when the registry has been reachable, and only JSON otherwise.
public class OutboxAvroTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public OutboxAvroTests(EmployeeTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    private sealed class StubEncoder(bool ready) : IEventAvroEncoder
    {
        public bool IsReady => ready;

        public bool TryEncode(string eventType, object payload, out byte[] bytes)
        {
            bytes = ready ? [0, 0, 0, 0, 3, 8] : [];
            return ready;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_hire_event_has_avro_only_when_the_encoder_was_ready(bool ready)
    {
        var hired = new EmployeeHiredEvent(Guid.NewGuid(), "Maria Chen", "maria@example.com", Guid.NewGuid(), Guid.NewGuid());
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
            new OutboxWriter(db, new StubEncoder(ready)).Enqueue("EmployeeHired", hired.EmployeeId.ToString(), hired);
            await db.SaveChangesAsync();
        }

        await using var read = _services.CreateAsyncScope();
        var row = await read.ServiceProvider.GetRequiredService<EmployeeDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync();
        Assert.Equal(ready ? new byte[] { 0, 0, 0, 0, 3, 8 } : null, row.AvroPayload);
        Assert.Contains("maria@example.com", row.Payload);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();
}
