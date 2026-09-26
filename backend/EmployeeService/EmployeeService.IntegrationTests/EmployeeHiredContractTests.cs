using System.Text.Json;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Outbox;

namespace EmployeeService.IntegrationTests;

// AuthService's EmployeeEventsConsumer deserializes this event into its own local
// EmployeeHiredEvent record (AuthService.Web/Consumers/EmployeeHiredEvent.cs) -
// deliberately not a shared type, so there's no compiler to catch a field rename
// here from breaking that consumer. This is the producer-side half of that
// contract: it fails the moment EmployeeId/FullName/Email stop being what
// HireEmployeeHandler actually puts on the wire, which is exactly when
// AuthService's consumer would start silently no-op'ing (unknown fields
// deserialize to their default, not an exception).
public class EmployeeHiredContractTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public EmployeeHiredContractTests(EmployeeTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task EmployeeHired_payload_carries_the_fields_AuthService_consumes()
    {
        var email = $"contract-{Guid.NewGuid():N}@test.local";
        var fullName = $"Contract Test Employee {Guid.NewGuid():N}";
        var employeeId = await HireAsync(email, fullName);

        var payload = await ExecuteInDb(db => db.Set<OutboxMessage>()
            .Where(m => m.Type == "EmployeeHired" && m.AggregateId == employeeId.ToString())
            .Select(m => m.Payload)
            .SingleAsync());

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;

        Assert.Equal(employeeId, root.GetProperty("EmployeeId").GetGuid());
        Assert.Equal(fullName, root.GetProperty("FullName").GetString());
        Assert.Equal(email, root.GetProperty("Email").GetString());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<Guid> HireAsync(string email, string fullName)
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>();
        var result = await handler.Handle(
            new HireEmployeeCommand(fullName, email, Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<T> ExecuteInDb<T>(Func<EmployeeDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        return await action(db);
    }
}
