using System.Text;
using System.Text.Json;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Consumers;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Outbox;

namespace AuthService.IntegrationTests;

// EmployeeService's AuthEventsConsumer deserializes these into its own local
// AccountProvisionedEvent/AccountProvisioningFailedEvent records (EmployeeService.
// Web/Consumers/) - deliberately not shared types, same reasoning as
// EmployeeHiredContractTests on the other side of this saga. These tests are the
// producer-side half: they fail the moment EmployeeEventsConsumer's outbox
// payloads stop carrying the field names that consumer expects.
public class AccountProvisioningContractTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly EmployeeEventsConsumer _sut;

    public AccountProvisioningContractTests(AuthTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _sut = new EmployeeEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthConsumerOptions()),
            _services.GetRequiredService<ILogger<EmployeeEventsConsumer>>());
    }

    [Fact]
    public async Task AccountProvisioned_payload_carries_the_fields_EmployeeService_consumes()
    {
        var employeeId = Guid.NewGuid();
        var email = $"contract-ok-{Guid.NewGuid():N}@test.local";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(BuildHiredResult(employeeId, "Contract Ok", email), CancellationToken.None));

        var payload = await ExecuteInDb(db => db.Set<OutboxMessage>()
            .Where(m => m.Type == "AccountProvisioned" && m.AggregateId == employeeId.ToString())
            .Select(m => m.Payload)
            .SingleAsync());

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;

        Assert.Equal(employeeId, root.GetProperty("EmployeeId").GetGuid());
        Assert.True(root.TryGetProperty("AccountId", out var accountId));
        Assert.NotEqual(Guid.Empty, accountId.GetGuid());
    }

    [Fact]
    public async Task AccountProvisioningFailed_payload_carries_the_fields_EmployeeService_consumes()
    {
        var existingEmail = $"contract-taken-{Guid.NewGuid():N}@test.local";
        await using (var scope = _services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AuthService.Domain.Account>>();
            var created = await userManager.CreateAsync(
                new AuthService.Domain.Account { UserName = existingEmail, Email = existingEmail }, "SomePassword123!");
            Assert.True(created.Succeeded);
        }

        var employeeId = Guid.NewGuid();
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildHiredResult(employeeId, "Contract Fail", existingEmail), CancellationToken.None));

        var payload = await ExecuteInDb(db => db.Set<OutboxMessage>()
            .Where(m => m.Type == "AccountProvisioningFailed" && m.AggregateId == employeeId.ToString())
            .Select(m => m.Payload)
            .SingleAsync());

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;

        Assert.Equal(employeeId, root.GetProperty("EmployeeId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("Reason").GetString()));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildHiredResult(Guid employeeId, string fullName, string email)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes("EmployeeHired") },
        };

        var payload = JsonSerializer.Serialize(new { EmployeeId = employeeId, FullName = fullName, Email = email });

        return new ConsumeResult<string, string>
        {
            Topic = "employee.events",
            Message = new Message<string, string> { Key = employeeId.ToString(), Value = payload, Headers = headers },
        };
    }

    private async Task<T> ExecuteInDb<T>(Func<AuthDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await action(db);
    }
}
