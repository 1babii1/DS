using AuditService.Domain;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Kafka;

namespace AuditService.Infrastructure;

// One consumer group across every producer's topic - this is the "second independent
// consumer" story: NotificationService runs its own group id against the same topics,
// so both see every event without stepping on each other's offsets.
//
// The consume loop, retry budget and dead-letter handling live in KafkaRetryConsumer;
// what remains here is only what "auditing a message" actually means.
public class AuditConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuditConsumerOptions> options,
    ILogger<AuditConsumer> logger)
    : KafkaRetryConsumer<AuditDbContext>(scopeFactory, options.Value, logger)
{
    protected override string MessageKind => "audit";

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
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        if (dbContext.Entries.Any(e => e.MessageId == messageGuid))
        {
            return Task.CompletedTask;
        }

        var sourceService = result.Topic.Replace(".events", string.Empty, StringComparison.Ordinal);
        var entry = AuditEntry.Create(
            messageGuid,
            sourceService,
            messageType ?? "Unknown",
            result.Message.Key,
            result.Message.Value,
            DateTime.UtcNow);

        dbContext.Entries.Add(entry);

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException) when (dbContext.Entries.Any(e => e.MessageId == messageGuid))
        {
            // Lost a race with another consumer instance on the unique index - fine, already recorded.
        }

        return Task.CompletedTask;
    }
}
