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
    private readonly AuditConsumerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, logger, stoppingToken, _options.Topics);

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

            try
            {
                ProcessMessage(result);
                consumer.Commit(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process audit message, will be redelivered");
            }
        }

        consumer.Close();
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
