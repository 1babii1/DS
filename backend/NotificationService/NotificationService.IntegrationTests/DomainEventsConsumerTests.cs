using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Domain;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web;
using NotificationService.Web.Consumers;

namespace NotificationService.IntegrationTests;

public class DomainEventsConsumerTests : IClassFixture<NotificationTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly DomainEventsConsumer _sut;

    public DomainEventsConsumerTests(NotificationTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _sut = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<IHubContext<NotificationsHub>>(),
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());
    }

    [Fact]
    public async Task AccountProvisioned_creates_the_lookup_and_a_welcome_notification()
    {
        var employeeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var payload = $$"""{"EmployeeId":"{{employeeId}}","AccountId":"{{accountId}}"}""";
        var result = BuildResult(Guid.NewGuid(), "auth.events", employeeId.ToString(), "AccountProvisioned", payload);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);

        var lookup = await ExecuteInDb(db => db.AccountLookups.SingleAsync(l => l.EmployeeId == employeeId));
        Assert.Equal(accountId, lookup.AccountId);

        var notification = await ExecuteInDb(
            db => db.Notifications.SingleAsync(n => n.RecipientAccountId == accountId));
        Assert.Equal("AccountProvisioned", notification.Type);
    }

    [Fact]
    public async Task CurrencyGranted_notifies_the_looked_up_account()
    {
        var employeeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await ExecuteInDb(async db =>
        {
            db.AccountLookups.Add(AccountLookup.Create(employeeId, accountId));
            await db.SaveChangesAsync();
            return true;
        });

        var payload = $$"""{"EmployeeId":"{{employeeId}}","Amount":100,"Reason":"Welcome bonus","NewBalance":100}""";
        var result = BuildResult(Guid.NewGuid(), "rewards.events", employeeId.ToString(), "CurrencyGranted", payload);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var notification = await ExecuteInDb(
            db => db.Notifications.SingleAsync(n => n.RecipientAccountId == accountId));
        Assert.Equal("CurrencyGranted", notification.Type);
    }

    [Fact]
    public async Task CurrencyGranted_with_no_account_lookup_yet_is_skipped_without_error()
    {
        var employeeId = Guid.NewGuid();
        var payload = $$"""{"EmployeeId":"{{employeeId}}","Amount":100,"Reason":"Welcome bonus","NewBalance":100}""";
        var result = BuildResult(Guid.NewGuid(), "rewards.events", employeeId.ToString(), "CurrencyGranted", payload);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var count = await ExecuteInDb(db => db.Notifications.CountAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AccountProvisioningFailed_without_an_actor_is_skipped_without_error()
    {
        var employeeId = Guid.NewGuid();
        var payload = $$"""{"EmployeeId":"{{employeeId}}","Reason":"duplicate email"}""";
        var result = BuildResult(
            Guid.NewGuid(), "auth.events", employeeId.ToString(), "AccountProvisioningFailed", payload);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var count = await ExecuteInDb(db => db.Notifications.CountAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AccountProvisioningFailed_with_an_actor_notifies_the_hiring_admin()
    {
        var employeeId = Guid.NewGuid();
        var hiredBy = Guid.NewGuid();
        var payload =
            $$"""{"EmployeeId":"{{employeeId}}","Reason":"duplicate email","HiredByAccountId":"{{hiredBy}}"}""";
        var result = BuildResult(
            Guid.NewGuid(), "auth.events", employeeId.ToString(), "AccountProvisioningFailed", payload);

        var handled = _sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None);

        Assert.True(handled);
        var notification = await ExecuteInDb(
            db => db.Notifications.SingleAsync(n => n.RecipientAccountId == hiredBy));
        Assert.Equal("AccountProvisioningFailed", notification.Type);
    }

    [Fact]
    public async Task Redelivering_the_same_message_id_does_not_duplicate_the_notification()
    {
        var employeeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var payload = $$"""{"EmployeeId":"{{employeeId}}","AccountId":"{{accountId}}"}""";
        var result = BuildResult(Guid.NewGuid(), "auth.events", employeeId.ToString(), "AccountProvisioned", payload);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var count = await ExecuteInDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == accountId));
        Assert.Equal(1, count);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildResult(
        Guid messageId, string topic, string key, string messageType, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(messageType) },
        };

        return new ConsumeResult<string, string>
        {
            Topic = topic,
            Message = new Message<string, string> { Key = key, Value = payload, Headers = headers },
        };
    }

    private async Task<T> ExecuteInDb<T>(Func<NotificationDbContext, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        return await action(db);
    }
}
