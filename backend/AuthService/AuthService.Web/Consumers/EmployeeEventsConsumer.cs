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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Outbox;

namespace AuthService.Web.Consumers;

// The AuthService side of the choreographed "Hire Employee" saga: on EmployeeHired,
// provision a login account and tell EmployeeService whether that succeeded so it
// can move the employee out of PendingProvisioning (or compensate). Structured
// identically to AuditService's AuditConsumer (retry, dead-letter, idempotent
// ProcessMessage) - same shape, different topic and different side effect.
public class EmployeeEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthConsumerOptions> options,
    ILogger<EmployeeEventsConsumer> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly AuthConsumerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, _options.Security, logger, stoppingToken, _options.Topics);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await Task.Run(() => Run(stoppingToken), stoppingToken);
    }

    private void Run(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        _options.Security.ApplyTo(config);

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.Topics);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result;
            try
            {
                result = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Kafka consume error");
                Thread.Sleep(TimeSpan.FromSeconds(1));
                continue;
            }

            if (result?.Message is null)
            {
                continue;
            }

            if (HandleWithRetryAndDeadLetter(result, stoppingToken))
            {
                consumer.Commit(result);
            }
            else
            {
                // Same reasoning as AuditConsumer: seek back so the next Consume()
                // call redelivers this exact message rather than silently skipping
                // past it once the database recovers.
                consumer.Seek(result.TopicPartitionOffset);

                try
                {
                    Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        consumer.Close();
    }

    internal bool HandleWithRetryAndDeadLetter(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                ProcessMessage(result);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(
                    ex,
                    "Failed to process employee event on {Topic} (attempt {Attempt}/{MaxAttempts})",
                    result.Topic,
                    attempt,
                    MaxAttempts);

                if (attempt < MaxAttempts)
                {
                    try
                    {
                        Task.Delay(RetryDelays[attempt - 1], stoppingToken).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }
        }

        return TryDeadLetter(result, lastError!);
    }

    private bool TryDeadLetter(ConsumeResult<string, string> result, Exception error)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            return true;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            if (dbContext.DeadLetters.Any(d => d.MessageId == messageGuid))
            {
                return true;
            }

            dbContext.DeadLetters.Add(DeadLetterEntry.Create(
                messageGuid,
                result.Topic,
                result.Message.Key,
                result.Message.Value,
                error.ToString(),
                MaxAttempts));

            dbContext.SaveChanges();

            logger.LogError(
                error,
                "Employee event {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
                messageGuid,
                result.Topic);

            return true;
        }
        catch (DbUpdateException) when (DeadLetterAlreadyRecorded(messageGuid))
        {
            return true;
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Failed to write dead letter for employee event {MessageId} on {Topic} - the database is likely down",
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return dbContext.DeadLetters.Any(d => d.MessageId == messageGuid);
    }

    private void ProcessMessage(ConsumeResult<string, string> result)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out _))
        {
            logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return;
        }

        if (messageType != EmployeeHiredEvent.MessageType)
        {
            // Nothing else on employee.events needs a login account provisioned.
            return;
        }

        var hired = JsonSerializer.Deserialize<EmployeeHiredEvent>(result.Message.Value)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeHiredEvent.MessageType} payload");

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var outboxWriter = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        var emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        // Idempotency: an EmployeeHired event redelivered after this already ran
        // (retry, restart, rebalance) must not attempt a second account.
        if (dbContext.Users.Any(a => a.EmployeeId == hired.EmployeeId))
        {
            return;
        }

        var temporaryPassword = GenerateTemporaryPassword();
        var account = new Account
        {
            UserName = hired.Email,
            Email = hired.Email,
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
                new AccountProvisioningFailedEvent(hired.EmployeeId, reason));
            dbContext.SaveChanges();
            logger.LogWarning(
                "Account provisioning failed for employee {EmployeeId}: {Reason}", hired.EmployeeId, reason);
            return;
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
            logger.LogWarning(ex, "Provisioned account for employee {EmployeeId} but failed to email the password", hired.EmployeeId);
        }
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

    private static string? GetHeader(Headers headers, string key)
    {
        if (!headers.TryGetLastBytes(key, out var bytes))
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
