using AuditService.Domain;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Avro;
using Shared.Kafka;
using Shared.Outbox;

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
    ILogger<AuditConsumer> logger,
    IEventAvroDecoder? avro = null)
    : KafkaRetryConsumer<AuditDbContext>(scopeFactory, options.Value, logger, avro)
{
    protected override string MessageKind => "audit";

    // When the event happened, as its producer recorded it. The consumer's own clock only says when the message
    // arrived, which is wrong for anything delayed, replayed or read after an outage; it is the fallback for messages
    // that carry no usable time (older ones, other producers).
    private static DateTime EventTime(ConsumeResult<string, string> result) =>
        result.Message.Headers.TryGetLastBytes(OutboxMessageHeaders.OccurredAt, out var bytes)
        && OutboxMessageHeaders.TryReadOccurredAt(bytes, out var occurredAt)
            ? occurredAt
            : DateTime.UtcNow;

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
        var vault = scope.ServiceProvider.GetRequiredService<PiiVault>();

        if (dbContext.RecordedMessages.Any(m => m.MessageId == messageGuid))
        {
            return Task.CompletedTask;
        }

        // "directory.events" and "directory.events.v2" are the same source.
        var sourceService = result.Topic.Replace(".v2", string.Empty, StringComparison.Ordinal)
            .Replace(".events", string.Empty, StringComparison.Ordinal);
        var schemaId = int.TryParse(GetHeader(result.Message.Headers, AvroSchemaIdHeader), out var id) ? id : (int?)null;
        var entry = AuditEntry.Create(
            messageGuid,
            sourceService,
            messageType ?? "Unknown",
            result.Message.Key,

            // The personal fields of the payload are stored encrypted under a key of the subject the event is about (ADR 0046).
            vault.Protect(result.Message.Key, messageType ?? "Unknown", result.Message.Value),
            EventTime(result),
            schemaId);

        // Both rows in one transaction: the message is recorded exactly when its entry is. RecordedMessages carries the
        // uniqueness the partitioned entries table cannot (ADR 0027).
        dbContext.RecordedMessages.Add(RecordedMessage.Create(messageGuid));
        dbContext.Entries.Add(entry);

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException) when (dbContext.RecordedMessages.AsNoTracking().Any(m => m.MessageId == messageGuid))
        {
            // Lost a race with another consumer instance on the unique index - fine, already recorded.
        }

        return Task.CompletedTask;
    }
}
