using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Domain;
using RewardsService.Infrastructure;
using Shared.Avro;
using Shared.Outbox;

namespace RewardsService.IntegrationTests;

// ADR 0023 on the money path: the grant, the ledger row and the event (JSON and, when the registry has been reachable,
// Avro) are one transaction, and the encoder is handed the event exactly as the JSON was made from it.
public class CurrencyGrantedAvroTests : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public CurrencyGrantedAvroTests(RewardsTestWebFactory factory)
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
            bytes = ready ? [0, 0, 0, 0, 9, 4, 5] : [];
            return ready;
        }
    }

    [Fact]
    public async Task A_grant_stores_the_avro_bytes_next_to_the_json_and_hands_the_encoder_the_event()
    {
        var encoder = new StubEncoder(ready: true);
        var employee = Guid.NewGuid();

        await Grant(encoder, employee, 40m);

        var row = await OnlyOutboxRow();
        Assert.Equal(new byte[] { 0, 0, 0, 0, 9, 4, 5 }, row.AvroPayload);
        var (type, payload) = Assert.Single(encoder.Seen);
        Assert.Equal("CurrencyGranted", type);
        Assert.Equal(employee, ((RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent)payload).EmployeeId);
        Assert.Equal(40m, ((RewardsService.Infrastructure.IntegrationEvents.CurrencyGrantedEvent)payload).NewBalance);
    }

    [Fact]
    public async Task Without_a_ready_encoder_the_grant_still_succeeds_with_json_only()
    {
        await Grant(new StubEncoder(ready: false), Guid.NewGuid(), 10m);

        var row = await OnlyOutboxRow();
        Assert.Null(row.AvroPayload);
        Assert.True(row.AvroExpected);
        Assert.Contains("CurrencyGranted", row.Type);
    }

    private async Task Grant(IEventAvroEncoder encoder, Guid employee, decimal amount)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        new CurrencyGrantWriter(db, encoder).Grant(employee, amount, "test", TransactionSource.ManualGrant, null);
        await db.SaveChangesAsync();
    }

    private async Task<OutboxMessage> OnlyOutboxRow()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        return await db.OutboxMessages.AsNoTracking().SingleAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();
}
