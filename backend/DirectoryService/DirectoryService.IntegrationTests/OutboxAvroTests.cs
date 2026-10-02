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
        Assert.True(row.AvroExpected);
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

        // Still owed to the Avro topic: the bytes are made from the JSON when it is published.
        Assert.True(row.AvroExpected);
        Assert.Contains(id.ToString(), row.Payload);
    }

    [Fact]
    public async Task With_no_encoder_at_all_the_row_is_what_it_always_was()
    {
        await Enqueue(null, new DepartmentDeletedEvent(Guid.NewGuid()));

        var row = await OnlyRow();
        Assert.Null(row.AvroPayload);
        Assert.False(row.AvroExpected);
    }

    // Which rows a publish cycle picks up, translated by EF and run by Postgres: the new columns must mean what the
    // publisher thinks they mean, in particular that rows from before Avro existed are never owed to the Avro topic.
    [Fact]
    public async Task The_pending_query_picks_the_rows_still_owed_to_each_topic()
    {
        OutboxMessage Row(string type) => OutboxMessage.Create(type, "agg", "{}");
        var fresh = Row("fresh");
        var freshAvro = Row("freshAvro");
        freshAvro.ExpectAvro([1]);
        var legacyDone = Row("legacyDone");
        legacyDone.MarkProcessed();
        var avroOwed = Row("avroOwed");
        avroOwed.ExpectAvro(null);
        avroOwed.MarkProcessed();
        var bothDone = Row("bothDone");
        bothDone.ExpectAvro([1]);
        bothDone.MarkProcessed();
        bothDone.MarkAvroPublished();
        var parked = Row("parked");
        parked.RecordFailure("x", maxAttempts: 1);
        var parkedAvroOwed = Row("parkedAvroOwed");
        parkedAvroOwed.ExpectAvro([1]);
        parkedAvroOwed.MarkProcessed();
        parkedAvroOwed.RecordFailure("x", maxAttempts: 1);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
            db.Set<OutboxMessage>().AddRange(fresh, freshAvro, legacyDone, avroOwed, bothDone, parked, parkedAvroOwed);
            await db.SaveChangesAsync();
        }

        await using var read = _services.CreateAsyncScope();
        var context = read.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        var withoutAvro = await context.Set<OutboxMessage>().Where(OutboxRowPublisher.Pending(false)).Select(m => m.Type).OrderBy(t => t).ToListAsync();
        var withAvro = await context.Set<OutboxMessage>().Where(OutboxRowPublisher.Pending(true)).Select(m => m.Type).OrderBy(t => t).ToListAsync();

        Assert.Equal(["fresh", "freshAvro"], withoutAvro);
        Assert.Equal(["avroOwed", "fresh", "freshAvro"], withAvro);
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
