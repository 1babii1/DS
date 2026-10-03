using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.Web.Consumers;
using AuthService.Web.Contracts;
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

        // Regression guard: RequireConfirmedAccount blocks PasswordSignInAsync for any
        // account with EmailConfirmed = false. An HR-provisioned account's email is
        // already trustworthy (it came from the hiring process, not self-registration),
        // so it must be pre-confirmed the same way OpenIddictSeeder's seed admin is -
        // otherwise every newly hired employee would be locked out on their first login.
        Assert.True(account.EmailConfirmed);

        var userManager = _services.CreateScope().ServiceProvider.GetRequiredService<UserManager<Account>>();
        Assert.True(await userManager.IsInRoleAsync(account, RoleNames.Viewer));

        Assert.Contains(_factory.EmailSender.Sent, s => s.ToEmail == email);

        var outboxCount = await ExecuteInDb(db => db.Set<Shared.Outbox.OutboxMessage>()
            .CountAsync(m => m.Type == "AccountProvisioned" && m.AggregateId == employeeId.ToString()));
        Assert.Equal(1, outboxCount);
    }

    [Fact]
    public async Task A_provisioned_employee_can_sign_in_with_the_emailed_temporary_password()
    {
        var employeeId = Guid.NewGuid();
        var email = $"login-after-hire-{Guid.NewGuid():N}@test.local";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildHiredResult(Guid.NewGuid(), employeeId, "New Hire", email), CancellationToken.None));

        var temporaryPassword = _factory.EmailSender.Sent.Single(s => s.ToEmail == email).Password;

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new LoginRequest(email, temporaryPassword));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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

    [Fact]
    public async Task A_compensation_locks_the_account_and_revokes_its_sessions_and_a_repeat_changes_nothing()
    {
        var employeeId = Guid.NewGuid();
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildHiredResult(Guid.NewGuid(), employeeId, "Undone Hire", $"undone-{Guid.NewGuid():N}@test.local"), CancellationToken.None));
        var stampBefore = (await ExecuteInDb(db => db.Users.SingleAsync(a => a.EmployeeId == employeeId))).SecurityStamp;

        Assert.True(_sut.HandleWithRetryAndDeadLetter(BuildCompensationResult(employeeId, revokeAccount: true), CancellationToken.None));
        var locked = await ExecuteInDb(db => db.Users.SingleAsync(a => a.EmployeeId == employeeId));
        Assert.Equal(DateTimeOffset.MaxValue, locked.LockoutEnd);
        Assert.NotEqual(stampBefore, locked.SecurityStamp);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(BuildCompensationResult(employeeId, revokeAccount: true), CancellationToken.None));
        Assert.Equal(locked.SecurityStamp, (await ExecuteInDb(db => db.Users.SingleAsync(a => a.EmployeeId == employeeId))).SecurityStamp);
    }

    [Fact]
    public async Task A_compensation_that_does_not_ask_for_the_account_leaves_it_alone()
    {
        var employeeId = Guid.NewGuid();
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildHiredResult(Guid.NewGuid(), employeeId, "Kept Hire", $"kept-{Guid.NewGuid():N}@test.local"), CancellationToken.None));

        Assert.True(_sut.HandleWithRetryAndDeadLetter(BuildCompensationResult(employeeId, revokeAccount: false), CancellationToken.None));

        Assert.Null((await ExecuteInDb(db => db.Users.SingleAsync(a => a.EmployeeId == employeeId))).LockoutEnd);
    }

    [Fact]
    public void A_compensation_for_an_account_that_was_never_made_is_nothing_to_undo()
    {
        Assert.True(_sut.HandleWithRetryAndDeadLetter(BuildCompensationResult(Guid.NewGuid(), revokeAccount: true), CancellationToken.None));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private static ConsumeResult<string, string> BuildCompensationResult(Guid employeeId, bool revokeAccount)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes("HireCompensationRequested") },
        };
        var payload = JsonSerializer.Serialize(new { EmployeeId = employeeId, Reason = "deadline", RevokeAccount = revokeAccount, ReverseBonus = false });
        return new ConsumeResult<string, string>
        {
            Topic = "employee.events",
            Message = new Message<string, string> { Key = employeeId.ToString(), Value = payload, Headers = headers },
        };
    }

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
