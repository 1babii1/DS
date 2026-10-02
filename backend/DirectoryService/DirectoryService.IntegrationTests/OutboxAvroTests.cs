using DirectoryService.Application.IntegrationEvents;
using DirectoryService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Avro;
using Shared.Outbox;

namespace DirectoryService.IntegrationTests;

// ADR 0023: when the schema registry has been reachable, the outbox row carries the event in Avro as well, written in the
// same transaction as the JSON; when it has not, the row still carries the JSON and nothing fails.
public class OutboxAvroTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public OutboxAvroTests(DirectoryTestWEbFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    private sealed class StubEncoder(bool ready) : IEventAvroEncoder
    {
        public bool IsConfigured => true;

        public bool IsReady => ready;

        public Task<byte[]> EncodeJsonAsync(string eventType, string json, CancellationToken cancellationToken) => throw new NotSupportedException();

        public List<(string Type, object Payload)> Seen { get; } = [];

        public bool TryEncode(string eventType, object payload, out byte[] bytes)
        {
            Seen.Add((eventType, payload));
            bytes = ready ? [0, 0, 0, 0, 7, 1, 2, 3] : [];
            return ready;
        }
    }

    [Fact]
    public async Task A_ready_encoder_adds_the_avro_bytes_next_to_the_json_in_the_same_row()
    {
        var encoder = new StubEncoder(ready: true);
        var id = Guid.NewGuid();

        await Enqueue(encoder, new DepartmentDeletedEvent(id));

        var row = await OnlyRow();
        Assert.Equal(new byte[] { 0, 0, 0, 0, 7, 1, 2, 3 }, row.AvroPayload);
        Assert.Contains(id.ToString(), row.Payload);
        Assert.Equal(("DepartmentDeleted", new DepartmentDeletedEvent(id)), Assert.Single(encoder.Seen));
    }

    [Fact]
    public async Task An_encoder_that_is_not_ready_leaves_the_json_alone_and_the_write_succeeds()
    {
        var id = Guid.NewGuid();

        await Enqueue(new StubEncoder(ready: false), new DepartmentDeletedEvent(id));

        var row = await OnlyRow();
        Assert.Null(row.AvroPayload);
        Assert.Contains(id.ToString(), row.Payload);
    }

    [Fact]
    public async Task With_no_encoder_at_all_the_row_is_what_it_always_was()
    {
        await Enqueue(null, new DepartmentDeletedEvent(Guid.NewGuid()));

        var row = await OnlyRow();
        Assert.Null(row.AvroPayload);
    }

    private async Task Enqueue(IEventAvroEncoder? encoder, DepartmentDeletedEvent payload)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        new OutboxWriter(db, encoder).Enqueue("DepartmentDeleted", payload.DepartmentId.ToString(), payload);
        await db.SaveChangesAsync();
    }

    private async Task<OutboxMessage> OnlyRow()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        return await db.Set<OutboxMessage>().AsNoTracking().SingleAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();
}
