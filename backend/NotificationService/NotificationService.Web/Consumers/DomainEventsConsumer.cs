using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationService.Domain;
using NotificationService.Infrastructure.Postgres;
using Shared.Kafka;

namespace NotificationService.Web.Consumers;

// One consumer group across every producer topic this service cares about. Lives in .Web
// rather than .Infrastructure.Postgres because it needs IHubContext<NotificationsHub>.
// Consume loop, retries and dead-lettering come from KafkaRetryConsumer.
public class DomainEventsConsumer(
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationsHub> hub,
    IOptions<DomainEventsConsumerOptions> options,
    ILogger<DomainEventsConsumer> logger)
    : KafkaRetryConsumer<NotificationDbContext>(scopeFactory, options.Value, logger)
{
    protected override string MessageKind => "notification";

    protected override Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            Logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return Task.CompletedTask;
        }

        using var scope = ScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();

        // Idempotency: a redelivered message must not create a second notification. Every
        // branch below either returns early via this check or is itself a no-op on
        // redelivery (the AccountLookup upsert).
        if (dbContext.Notifications.Any(n => n.SourceMessageId == messageGuid))
        {
            return Task.CompletedTask;
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
            return Task.CompletedTask;
        }

        dbContext.Notifications.Add(notification);

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException) when (dbContext.Notifications.Any(n => n.SourceMessageId == messageGuid))
        {
            // Lost a race with another consumer instance - fine, already recorded.
            return Task.CompletedTask;
        }

        PushAsync(notification).GetAwaiter().GetResult();

        return Task.CompletedTask;
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
}
