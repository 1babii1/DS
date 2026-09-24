using AuditService.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shared.Ops;

namespace AuditService.IntegrationTests;

// A consumer-only service has no outbox table. Reading one anyway would throw on every metrics
// refresh, so the reader must skip it and still report dead letters.
public class OpsSnapshotTests : IClassFixture<AuditTestWebFactory>
{
    private readonly IServiceProvider _services;

    public OpsSnapshotTests(AuditTestWebFactory factory) => _services = factory.Services;

    [Fact]
    public async Task A_service_without_an_outbox_reports_zero_outbox_figures_instead_of_failing()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var snapshot = await OpsSnapshotReader.ReadAsync(db, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(0, snapshot.Parked);
        Assert.Equal(0, snapshot.Pending);
        Assert.Equal(0d, snapshot.OldestPendingAgeSeconds);
        Assert.NotNull(snapshot.DeadLetters);
    }
}
