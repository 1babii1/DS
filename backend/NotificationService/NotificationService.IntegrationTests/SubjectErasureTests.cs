using System.Reflection;
using System.Text;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Infrastructure.Postgres;
using NotificationService.Web;
using NotificationService.Web.Consumers;
using NotificationService.Web.Controllers;

namespace NotificationService.IntegrationTests;

// Erasing a person (ADR 0047): what this service holds about them goes, and nothing for them is created again. Real consumer, real Postgres.
public class SubjectErasureTests : IClassFixture<NotificationTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly DomainEventsConsumer _consumer;

    public SubjectErasureTests(NotificationTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _consumer = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<IHubContext<NotificationsHub>>(),
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private void Consume(string topic, string key, string type, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(type) },
        };
        Assert.True(_consumer.HandleWithRetryAndDeadLetter(
            new ConsumeResult<string, string>
            {
                Topic = topic,
                Message = new Message<string, string> { Key = key, Value = payload, Headers = headers },
            },
            CancellationToken.None));
    }

    private (Guid Employee, Guid Account) Person()
    {
        var employee = Guid.NewGuid();
        var account = Guid.NewGuid();
        Consume("auth.events", employee.ToString(), "AccountProvisioned", $$"""{"EmployeeId":"{{employee}}","AccountId":"{{account}}"}""");
        Consume("rewards.events", employee.ToString(), "CurrencyGranted", $$"""{"EmployeeId":"{{employee}}","Amount":10,"Reason":"Spot","NewBalance":10}""");
        return (employee, account);
    }

    private async Task<T> InDb<T>(Func<NotificationDbContext, Task<T>> work)
    {
        await using var scope = _services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<NotificationDbContext>());
    }

    private async Task<ErasureResult> Erase(params string[] subjects)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SubjectErasure>().EraseAsync(subjects, CancellationToken.None);
    }

    [Fact]
    public async Task Erasing_an_employee_removes_the_notifications_of_their_account_and_the_link()
    {
        var (employee, account) = Person();
        Assert.Equal(2, await InDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == account)));

        var result = await Erase(employee.ToString());

        Assert.Equal(2, result.Notifications);
        Assert.Equal(1, result.Lookups);
        Assert.Equal(0, await InDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == account)));
        Assert.Equal(0, await InDb(db => db.AccountLookups.CountAsync(l => l.EmployeeId == employee)));
    }

    [Fact]
    public async Task Erasing_by_account_id_finds_the_same_person_through_the_link()
    {
        var (employee, account) = Person();

        var result = await Erase(account.ToString());

        Assert.Equal(2, result.Notifications);
        Assert.Equal(0, await InDb(db => db.AccountLookups.CountAsync(l => l.EmployeeId == employee)));
    }

    [Fact]
    public async Task Another_person_is_left_alone()
    {
        var gone = Person();
        var kept = Person();

        await Erase(gone.Employee.ToString());

        Assert.Equal(2, await InDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == kept.Account)));
        Assert.Equal(1, await InDb(db => db.AccountLookups.CountAsync(l => l.EmployeeId == kept.Employee)));
    }

    [Fact]
    public async Task A_late_event_for_an_erased_person_creates_nothing()
    {
        var (employee, account) = Person();
        await Erase(employee.ToString());

        Consume("rewards.events", employee.ToString(), "CurrencyGranted", $$"""{"EmployeeId":"{{employee}}","Amount":5,"Reason":"Late","NewBalance":5}""");
        Consume("auth.events", employee.ToString(), "AccountProvisioned", $$"""{"EmployeeId":"{{employee}}","AccountId":"{{account}}"}""");

        Assert.Equal(0, await InDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == account)));
        Assert.Equal(0, await InDb(db => db.AccountLookups.CountAsync(l => l.EmployeeId == employee)));
    }

    [Fact]
    public async Task A_notification_addressed_to_an_erased_account_is_not_created_even_when_the_event_is_about_someone_else()
    {
        var hirer = Guid.NewGuid();
        var other = Guid.NewGuid();
        await Erase(hirer.ToString());

        // The failure notice goes to whoever hired the person, and the event is keyed by the person hired, not by the recipient.
        Consume("auth.events", other.ToString(), "AccountProvisioningFailed", $$"""{"EmployeeId":"{{other}}","Reason":"duplicate email","HiredByAccountId":"{{hirer}}"}""");

        Assert.Equal(0, await InDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == hirer)));
    }

    [Fact]
    public async Task The_same_failure_notice_does_reach_a_hirer_who_was_not_erased()
    {
        var hirer = Guid.NewGuid();
        var other = Guid.NewGuid();

        Consume("auth.events", other.ToString(), "AccountProvisioningFailed", $$"""{"EmployeeId":"{{other}}","Reason":"duplicate email","HiredByAccountId":"{{hirer}}"}""");

        Assert.Equal(1, await InDb(db => db.Notifications.CountAsync(n => n.RecipientAccountId == hirer)));
    }

    [Fact]
    public async Task Erasing_twice_is_harmless()
    {
        var (employee, _) = Person();

        await Erase(employee.ToString());
        var second = await Erase(employee.ToString());

        Assert.Equal(0, second.Notifications);
        Assert.Equal(0, second.Lookups);
    }

    [Fact]
    public async Task The_erase_endpoint_refuses_none_and_too_many_subjects()
    {
        await using var scope = _services.CreateAsyncScope();
        var controller = new SubjectsController(scope.ServiceProvider.GetRequiredService<SubjectErasure>());

        var none = await controller.Erase(new EraseSubjectsRequest([]), CancellationToken.None);
        var many = await controller.Erase(
            new EraseSubjectsRequest(Enumerable.Range(0, 51).Select(i => Guid.NewGuid().ToString()).ToArray()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(none.Result);
        Assert.IsType<BadRequestObjectResult>(many.Result);
    }

    [Fact]
    public async Task Every_policy_a_controller_asks_for_is_registered()
    {
        var provider = _services.GetRequiredService<IAuthorizationPolicyProvider>();
        var named = typeof(SubjectsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<AuthorizeAttribute>(true)
                .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(true))))
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();

        Assert.Contains("StepUp", named);
        foreach (var policy in named)
        {
            Assert.True(await provider.GetPolicyAsync(policy!) is not null, $"policy '{policy}' is asked for by a controller and never registered");
        }
    }
}
