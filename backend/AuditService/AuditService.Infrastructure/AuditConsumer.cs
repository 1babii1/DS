using System.Text;
using AuditService.Domain;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Outbox;

namespace AuditService.Infrastructure;

// One consumer group across every producer's topic - this is the "second independent
// consumer" story: NotificationService runs its own group id against the same topics,
// so both see every event without stepping on each other's offsets.
public class AuditConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuditConsumerOptions> options,
    ILogger<AuditConsumer> logger) : BackgroundService
{
    // Bounded retries in place, before the offset is committed: Consume() always moves
    // forward regardless of commit, so retrying only works by not fetching the next
    // message until this one is dealt with. Enough attempts to ride out a brief Postgres
    // reconnect, not so many that a real outage stalls the whole log for minutes.
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly AuditConsumerOptions _options = options.Value;

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
                // Both retries and the dead-letter write failed - almost certainly the
                // database itself is down. Seeking back means the next Consume() call
                // returns this exact message again instead of silently skipping past it,
                // so the audit log stalls here rather than losing the record. Nothing
                // committed so far is lost: earlier entries already have their offsets
                // committed, and this one gets replayed - safely, since writing it is
                // idempotent by MessageId - once the database recovers.
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

    /// <returns>
    /// True once the message is durably accounted for - either recorded normally or
    /// parked in dead_letters - and it is safe to commit its offset.
    /// </returns>
    /// <remarks>
    /// Internal, not private: this is the entire retry/dead-letter decision, with no
    /// dependency on a live Kafka broker (it only reads ConsumeResult as a data
    /// holder), so AuditService.IntegrationTests calls it directly against a real
    /// Postgres via Testcontainers rather than needing a Kafka test harness too.
    /// </remarks>
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
                    "Failed to process audit message on {Topic} (attempt {Attempt}/{MaxAttempts})",
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
            // No id to key the dead letter on either - already logged in ProcessMessage.
            // There is nothing further to retry for a message shaped like this.
            return true;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

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
                "Audit message {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
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
                "Failed to write dead letter for audit message {MessageId} on {Topic} - the database is likely down",
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
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
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        if (dbContext.Entries.Any(e => e.MessageId == messageGuid))
        {
            return;
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