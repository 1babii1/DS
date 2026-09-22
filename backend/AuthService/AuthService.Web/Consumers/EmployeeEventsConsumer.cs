using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Application.Database;
using AuthService.Application.IntegrationEvents;
using AuthService.Domain;
using AuthService.Infrastructure.Postgres;
using AuthService.Web.Configuration;
using Confluent.Kafka;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Kafka;

namespace AuthService.Web.Consumers;

// The AuthService side of the choreographed "Hire Employee" saga: on EmployeeHired,
// provision a login account and tell EmployeeService whether that succeeded so it can move
// the employee out of PendingProvisioning (or compensate). Consume loop, retries and
// dead-lettering come from KafkaRetryConsumer.
public class EmployeeEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthConsumerOptions> options,
    ILogger<EmployeeEventsConsumer> logger)
    : KafkaRetryConsumer<AuthDbContext>(scopeFactory, options.Value, logger)
{
    protected override string MessageKind => "auth";

    protected override Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out _))
        {
            Logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return Task.CompletedTask;
        }

        if (messageType != EmployeeHiredEvent.MessageType)
        {
            // Nothing else on employee.events needs a login account provisioned.
            return Task.CompletedTask;
        }

        var hired = JsonSerializer.Deserialize<EmployeeHiredEvent>(result.Message.Value)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeHiredEvent.MessageType} payload");

        using var scope = ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var outboxWriter = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        // Idempotency: an EmployeeHired event redelivered after this already ran
        // (retry, restart, rebalance) must not attempt a second account.
        if (dbContext.Users.Any(a => a.EmployeeId == hired.EmployeeId))
        {
            return Task.CompletedTask;
        }

        var temporaryPassword = GenerateTemporaryPassword();
        var account = new Account
        {
            UserName = hired.Email,
            Email = hired.Email,

            // Same reasoning as OpenIddictSeeder's seed admin: an HR-provisioned account's
            // email came from the hiring process, not a self-asserted registration form, so
            // it is already trustworthy. Leaving this false would have silently locked every
            // newly hired employee out - RequireConfirmedAccount blocks PasswordSignInAsync
            // for any account with EmailConfirmed = false, temporary password or not.
            EmailConfirmed = true,
            EmployeeId = hired.EmployeeId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var createResult = userManager.CreateAsync(account, temporaryPassword).GetAwaiter().GetResult();
        if (!createResult.Succeeded)
        {
            var reason = string.Join("; ", createResult.Errors.Select(e => e.Description));
            outboxWriter.Enqueue(
                AuthEventTypes.AccountProvisioningFailed,
                hired.EmployeeId.ToString(),
                new AccountProvisioningFailedEvent(hired.EmployeeId, reason, hired.HiredByAccountId));
            dbContext.SaveChanges();
            Logger.LogWarning(
                "Account provisioning failed for employee {EmployeeId}: {Reason}", hired.EmployeeId, reason);
            return Task.CompletedTask;
        }

        userManager.AddToRoleAsync(account, RoleNames.Viewer).GetAwaiter().GetResult();

        // UserManager.CreateAsync/AddToRoleAsync each auto-save through the EF store
        // (UserStore.AutoSaveChanges defaults to true, and turning it off to batch
        // this with the outbox write fought Identity's internal FK ordering more
        // than it was worth). The account is durable by this point; only the
        // AccountProvisioned event's own commit is a separate write. The
        // consequence of a crash in that narrow window is an account with no event
        // ever published - EmployeeId's unique index still makes a retry of the
        // whole ProcessMessage safe (it would just see the account already exists),
        // but nothing currently re-drives that retry automatically; an ops-visible
        // gap, not a silent one, since the employee stays in PendingProvisioning.
        outboxWriter.Enqueue(
            AuthEventTypes.AccountProvisioned,
            hired.EmployeeId.ToString(),
            new AccountProvisionedEvent(hired.EmployeeId, account.Id));
        dbContext.SaveChanges();

        // Credential delivery is a separate concern from provisioning: the account
        // and the AccountProvisioned event are already durable above, so a failed
        // send here is an ops problem (retry manually, or the employee uses "forgot
        // password" once that exists) - not a reason to compensate a saga step that
        // already succeeded.
        try
        {
            emailSender.SendNewAccountPasswordAsync(hired.Email, temporaryPassword, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Provisioned account for employee {EmployeeId} but failed to email the password", hired.EmployeeId);
        }

        return Task.CompletedTask;
    }

    private static string GenerateTemporaryPassword()
    {
        // Satisfies AuthenticationConfiguration's password policy (length 8+, upper,
        // lower, digit) by construction rather than retrying against the validator.
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string all = upper + lower + digits;

        var chars = new char[16];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 3; i < chars.Length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
