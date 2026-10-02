using AuthService.Application.IntegrationEvents;
using AuthService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Avro;
using Shared.Outbox;

namespace AuthService.IntegrationTests;

// ADR 0023: AuthService's events carry Avro next to their JSON when the registry has been reachable, and only JSON otherwise.
public class OutboxAvroTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly AuthTestWebFactory _factory;

    public OutboxAvroTests(AuthTestWebFactory factory) => _factory = factory;

    private sealed class StubEncoder(bool ready) : IEventAvroEncoder
    {
        public bool IsConfigured => true;

        public bool IsReady => ready;

        public Task<byte[]> EncodeJsonAsync(string eventType, string json, CancellationToken cancellationToken) => throw new NotSupportedException();

        public bool TryEncode(string eventType, object payload, out byte[] bytes)
        {
            bytes = ready ? [0, 0, 0, 0, 5, 6] : [];
            return ready;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_provisioning_event_has_avro_only_when_the_encoder_was_ready(bool ready)
    {
        var provisioned = new AccountProvisionedEvent(Guid.NewGuid(), Guid.NewGuid());
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            new OutboxWriter(db, new StubEncoder(ready)).Enqueue("AccountProvisioned", provisioned.EmployeeId.ToString(), provisioned);
            await db.SaveChangesAsync();
        }

        await using var read = _factory.Services.CreateAsyncScope();
        var row = await read.ServiceProvider.GetRequiredService<AuthDbContext>().Set<OutboxMessage>().AsNoTracking()
            .SingleAsync(m => m.Type == "AccountProvisioned");
        Assert.Equal(ready ? new byte[] { 0, 0, 0, 0, 5, 6 } : null, row.AvroPayload);
        Assert.True(row.AvroExpected);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _factory.ResetDatabaseAsync();
}
