using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Domain;
using NotificationService.Infrastructure.Postgres;
using Shared.Outbox;

namespace NotificationService.Web.Consumers;

// One consumer group across every producer's topic this service cares about - structural
// copy of AuditConsumer/EmployeeService's AuthEventsConsumer/RewardsService's
// WelcomeBonusConsumer (retry, dead-letter, idempotent ProcessMessage,
// HandleWithRetryAndDeadLetter as the internal seam for direct-call tests). Lives in
// .Web rather than .Infrastructure.Postgres because it needs IHubContext<NotificationsHub>
// - the same reason EmployeeService's AuthEventsConsumer lives in EmployeeService.Web,
// not its Infrastructure.Postgres project.
public class DomainEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationsHub> hub,
    IOptions<DomainEventsConsumerOptions> options,
    ILogger<DomainEventsConsumer> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly DomainEventsConsumerOptions _options = options.Value;

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
                    "Failed to process notification message on {Topic} (attempt {Attempt}/{MaxAttempts})",
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
            var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

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
                "Notification message {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
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
                "Failed to write dead letter for notification message {MessageId} on {Topic} - the database is likely down",
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        return dbContext.DeadLetters.Any(d => d.MessageId == messageGuid);
    }

    private void ProcessMessage(ConsumeResult<string, string> result)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

        // Idempotency: a redelivered message must not create a second notification. Every
        // branch below either returns early via this check or is itself a no-op on
        // redelivery (the AccountLookup upsert).
        if (dbContext.Notifications.Any(n => n.SourceMessageId == messageGuid))
        {
            return;
        }

        Notification? notification = messageType switch
        {
            CurrencyGrantedEvent.MessageType => HandleCurrencyGranted(dbContext, messageGuid, result.Message.Value),
            AccountProvisionedEvent.MessageType => HandleAccountProvisioned(dbContext, messageGuid, result.Message.Value),
            AccountProvisioningFailedEvent.MessageType => HandleAccountProvisioningFailed(dbContext, messageGuid, result.Message.Value),
            EmployeeTransferredEvent.MessageType => HandleEmployeeTransferred(dbContext, messageGuid, result.Message.Value),
            _ => null,
        };

        if (notification is null)
        {
            return;
        }

        dbContext.Notifications.Add(notification);

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException) when (dbContext.Notifications.Any(n => n.SourceMessageId == messageGuid))
        {
            // Lost a race with another consumer instance - fine, already recorded.
            return;
        }

        PushAsync(notification).GetAwaiter().GetResult();
    }

    private static Notification? HandleCurrencyGranted(NotificationDbContext dbContext, Guid messageId, string payload)
    {
        var @event = JsonSerializer.Deserialize<CurrencyGrantedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {CurrencyGrantedEvent.MessageType} payload");

        var accountId = FindAccountId(dbContext, @event.EmployeeId);
        if (accountId is null)
        {
            // No account provisioned yet for this employee - nothing to notify. Not an
            // error: a manual grant can, in principle, race ahead of provisioning.
            return null;
        }

        return Notification.Create(
            messageId,
            accountId.Value,
            CurrencyGrantedEvent.MessageType,
            "Вам начислена валюта",
            $"{@event.Reason}: +{@event.Amount} (баланс: {@event.NewBalance})");
    }

    private static Notification HandleAccountProvisioned(NotificationDbContext dbContext, Guid messageId, string payload)
    {
        var @event = JsonSerializer.Deserialize<AccountProvisionedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisionedEvent.MessageType} payload");

        if (!dbContext.AccountLookups.Any(l => l.EmployeeId == @event.EmployeeId))
        {
            dbContext.AccountLookups.Add(AccountLookup.Create(@event.EmployeeId, @event.AccountId));
        }

        return Notification.Create(
            messageId,
            @event.AccountId,
            AccountProvisionedEvent.MessageType,
            "Добро пожаловать!",
            "Ваш аккаунт создан. Вы можете войти в систему.");
    }

    private static Notification? HandleAccountProvisioningFailed(NotificationDbContext dbContext, Guid messageId, string payload)
    {
        var @event = JsonSerializer.Deserialize<AccountProvisioningFailedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {AccountProvisioningFailedEvent.MessageType} payload");

        // No actor recorded (an employee hired before actor tracking existed) - nobody to
        // notify but the dead-letter/log trail, same as before this feature existed.
        if (@event.HiredByAccountId is null)
        {
            return null;
        }

        return Notification.Create(
            messageId,
            @event.HiredByAccountId.Value,
            AccountProvisioningFailedEvent.MessageType,
            "Не удалось создать аккаунт",
            $"Сотрудник {@event.EmployeeId}: {@event.Reason}");
    }

    private static Notification? HandleEmployeeTransferred(NotificationDbContext dbContext, Guid messageId, string payload)
    {
        var @event = JsonSerializer.Deserialize<EmployeeTransferredEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeTransferredEvent.MessageType} payload");

        var accountId = FindAccountId(dbContext, @event.EmployeeId);
        if (accountId is null)
        {
            return null;
        }

        return Notification.Create(
            messageId,
            accountId.Value,
            EmployeeTransferredEvent.MessageType,
            "Вы переведены",
            $"Новый отдел: {@event.DepartmentId}, новая позиция: {@event.PositionId}");
    }

    private static Guid? FindAccountId(NotificationDbContext dbContext, Guid employeeId) =>
        dbContext.AccountLookups.Where(l => l.EmployeeId == employeeId).Select(l => (Guid?)l.AccountId).SingleOrDefault();

    private Task PushAsync(Notification notification) =>
        hub.Clients.Group(notification.RecipientAccountId.ToString()).SendAsync(
            "notification",
            new
            {
                notification.Id,
                notification.Type,
                notification.Title,
                notification.Body,
                notification.DeepLink,
                notification.CreatedAt,
            });

    private static string? GetHeader(Headers headers, string key)
    {
        if (!headers.TryGetLastBytes(key, out var bytes))
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
