using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using EmployeeService.Application.Employees.Commands;
using EmployeeService.Domain;
using EmployeeService.Infrastructure.Postgres;
using EmployeeService.Web.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmployeeService.IntegrationTests;

// The EmployeeService half of the "Hire Employee" saga - exercises
// AuthEventsConsumer.HandleWithRetryAndDeadLetter directly against the real
// Postgres this factory spins up, same seam AuditConsumerTests/
// EmployeeEventsConsumerTests use, so none of this needs a live Kafka broker.
public class AuthEventsConsumerTests : IClassFixture<EmployeeTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly AuthEventsConsumer _sut;

    public AuthEventsConsumerTests(EmployeeTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _sut = new AuthEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmployeeConsumerOptions()),
            _services.GetRequiredService<ILogger<AuthEventsConsumer>>());
    }

    [Fact]
    public async Task AccountProvisioned_moves_a_pending_employee_to_active()
    {
        var employeeId = await HireAsync();
        var result = BuildResult(Guid.NewGuid(), "AccountProvisioned", employeeId, new { EmployeeId = employeeId, AccountId = Guid.NewGuid() });

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == employeeId));
        Assert.Equal(EmployeeStatus.Active, stored.Status);
    }

    [Fact]
    public async Task AccountProvisioningFailed_moves_a_pending_employee_to_failed_with_a_reason()
    {
        var employeeId = await HireAsync();
        var result = BuildResult(
            Guid.NewGuid(), "AccountProvisioningFailed", employeeId, new { EmployeeId = employeeId, Reason = "Email already taken" });

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == employeeId));
        Assert.Equal(EmployeeStatus.ProvisioningFailed, stored.Status);
        Assert.Equal("Email already taken", stored.ProvisioningFailureReason);
    }

    [Fact]
    public async Task Redelivering_AccountProvisioned_after_it_already_applied_is_a_no_op()
    {
        var employeeId = await HireAsync();
        var result = BuildResult(Guid.NewGuid(), "AccountProvisioned", employeeId, new { EmployeeId = employeeId, AccountId = Guid.NewGuid() });

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var stored = await ExecuteInDb(db => db.Employees.SingleAsync(e => e.Id == employeeId));
        Assert.Equal(EmployeeStatus.Active, stored.Status);
    }

    [Fact]
    public async Task AccountProvisioned_for_an_unknown_employee_is_ignored_without_error()
    {
        var result = BuildResult(Guid.NewGuid(), "AccountProvisioned", Guid.NewGuid(), new { EmployeeId = Guid.NewGuid(), AccountId = Guid.NewGuid() });

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private async Task<Guid> HireAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HireEmployeeHandler>();
        var command = new HireEmployeeCommand(
            $"Saga Test Employee {Guid.NewGuid():N}",
            $"saga-{Guid.NewGuid():N}@test.local",
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.Handle(command, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static ConsumeResult<string, string> BuildResult(Guid messageId, string messageType, Guid employeeId, object payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(messageType) },
        };

        return new ConsumeResult<string, string>
        {
            Topic = "auth.events",
            Message = new Message<string, string>
            {
                Key = employeeId.ToString(),
                Value = JsonSerializer.Serialize(payload),
                Headers = headers,
            },
        };
    }

    private async Task<T> ExecuteInDb<T>(Func<EmployeeDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        return await action(db);
    }
}
