using System.Text;
using System.Text.Json;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.Web.Consumers;
using Confluent.Kafka;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthService.IntegrationTests;

// The AuthService half of the "Hire Employee" saga - exercises
// EmployeeEventsConsumer.HandleWithRetryAndDeadLetter directly against the real
// Postgres this factory already spins up (Testcontainers), same seam
// AuditConsumerTests uses, so none of this needs a live Kafka broker.
public class EmployeeEventsConsumerTests : IClassFixture<AuthTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly AuthTestWebFactory _factory;
    private readonly EmployeeEventsConsumer _sut;

    public EmployeeEventsConsumerTests(AuthTestWebFactory factory)
    {
        _factory = factory;
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _sut = new EmployeeEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthConsumerOptions()),
            _services.GetRequiredService<ILogger<EmployeeEventsConsumer>>());
    }

    [Fact]
    public async Task EmployeeHired_provisions_an_account_and_emails_the_password()
    {
        var employeeId = Guid.NewGuid();
        var email = $"hire-{Guid.NewGuid():N}@test.local";
        var result = BuildHiredResult(Guid.NewGuid(), employeeId, "Jane Doe", email);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);

        var account = await ExecuteInDb(db => db.Users.SingleAsync(a => a.EmployeeId == employeeId));
        Assert.Equal(email, account.Email);

        var userManager = _services.CreateScope().ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.True(await userManager.IsInRoleAsync(account, RoleNames.Viewer));

        Assert.Contains(_factory.EmailSender.Sent, s => s.ToEmail == email);

        var outboxCount = await ExecuteInDb(db => db.Set<Shared.Outbox.OutboxMessage>()
            .CountAsync(m => m.Type == "AccountProvisioned" && m.AggregateId == employeeId.ToString()));
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public async Task EmployeeHired_for_an_email_that_already_has_an_account_fails_provisioning()
    {
        var existingEmail = $"taken-{Guid.NewGuid():N}@test.local";
        await using (var scope = _services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var created = await userManager.CreateAsync(
                new Account { UserName = existingEmail, Email = existingEmail }, "SomePassword123!");
            Assert.True(created.Succeeded);
        }

        var employeeId = Guid.NewGuid();
        var result = BuildHiredResult(Guid.NewGuid(), employeeId, "Collision Case", existingEmail);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);

        var provisionedForThisEmployee = await ExecuteInDb(
            db => db.Users.CountAsync(a => a.EmployeeId == employeeId));
        Assert.Equal(0, provisionedForThisEmployee);

        var outboxCount = await ExecuteInDb(db => db.Set<Shared.Outbox.OutboxMessage>()
            .CountAsync(m => m.Type == "AccountProvisioningFailed" && m.AggregateId == employeeId.ToString()));
        Assert.Equal(1, outboxCount);
        // Nothing was provisioned, so nothing should have been emailed for this hire.
        Assert.DoesNotContain(_factory.EmailSender.Sent, s => s.ToEmail == existingEmail);
    }

    [Fact]
    public async Task Redelivering_the_same_EmployeeHired_event_does_not_provision_a_second_account()
    {
        var employeeId = Guid.NewGuid();
        var email = $"redeliver-{Guid.NewGuid():N}@test.local";
        var result = BuildHiredResult(Guid.NewGuid(), employeeId, "Redeliver Case", email);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var count = await ExecuteInDb(db => db.Users.CountAsync(a => a.EmployeeId == employeeId));
        Assert.Equal(1, count);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildHiredResult(
        Guid messageId, Guid employeeId, string fullName, string email)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
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
