using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Infrastructure;
using Shared.Avro;
using Shared.IntegrationEvents;
using Shared.Ops;
using Shared.Outbox;

namespace RewardsService.IntegrationTests;

// The operator's redrive writes an audit event to the service's own outbox. It is an event like any other, so it has a
// schema (owned by the shared code) and is owed to the Avro topic when a registry is configured.
public class RedriveAvroTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public RedriveAvroTests(RewardsTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    private sealed class StubEncoder : IEventAvroEncoder
    {
        public bool IsConfigured => true;

        public bool IsReady => true;

        public List<(string Type, object Payload)> Seen { get; } = [];

        public bool TryEncode(string eventType, object payload, out byte[] bytes)
        {
            Seen.Add((eventType, payload));
            bytes = [4, 2];
            return true;
        }

        public Task<byte[]> EncodeJsonAsync(string eventType, string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_redrive_audit_event_is_encoded_with_who_did_it_and_which_message()
    {
        var parked = OutboxMessage.Create("CurrencyGranted", "agg", "{}");
        parked.RecordFailure("x", maxAttempts: 1);
        await using (var seed = _services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<RewardsDbContext>();
            db.Set<OutboxMessage>().Add(parked);
            await db.SaveChangesAsync();
        }

        var encoder = new StubEncoder();
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
            Assert.Equal(RedriveOutcome.Redriven, await OpsHandlers.RedriveAsync(db, parked.Id, "admin-42", CancellationToken.None, encoder));
        }

        var (type, payload) = Assert.Single(encoder.Seen);
        Assert.Equal("OutboxMessageRedriven", type);
        Assert.Equal(new OutboxMessageRedrivenEvent(parked.Id, "CurrencyGranted", "admin-42"), payload);

        await using var verify = _services.CreateAsyncScope();
        var audit = await verify.ServiceProvider.GetRequiredService<RewardsDbContext>().Set<OutboxMessage>()
            .SingleAsync(m => m.Type == "OutboxMessageRedriven");
        Assert.Equal(new byte[] { 4, 2 }, audit.AvroPayload);
        Assert.Contains("admin-42", audit.Payload);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();
}
