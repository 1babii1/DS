using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RewardsService.Domain;
using Shared.Outbox;

namespace RewardsService.Infrastructure.Consumers;

// Structural copy of AuditConsumer/EmployeeService's AuthEventsConsumer (retry, dead
// letter, HandleWithRetryAndDeadLetter as the internal seam for direct-call tests
// without a live Kafka broker). Only listens on employee.events, only acts on
// EmployeeHired - everything else on that topic is silently ignored.
public class WelcomeBonusConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<WelcomeBonusConsumerOptions> options,
    ILogger<WelcomeBonusConsumer> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly WelcomeBonusConsumerOptions _options = options.Value;

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
                    "Failed to process rewards message on {Topic} (attempt {Attempt}/{MaxAttempts})",
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
            var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();

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
                "Rewards message {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
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
                "Failed to write dead letter for rewards message {MessageId} on {Topic} - the database is likely down",
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
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

        if (messageType != EmployeeHiredEvent.MessageType)
        {
            // Nothing else on employee.events triggers a welcome bonus.
            return;
        }

        var hired = JsonSerializer.Deserialize<EmployeeHiredEvent>(result.Message.Value)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeHiredEvent.MessageType} payload");

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        var writer = new CurrencyGrantWriter(dbContext);

        // Idempotency: an EmployeeHired event redelivered after this already ran must
        // not grant a second welcome bonus.
        if (dbContext.Transactions.Any(t => t.EmployeeId == hired.EmployeeId && t.Source == TransactionSource.WelcomeBonus))
        {
            return;
        }

        writer.Grant(hired.EmployeeId, _options.WelcomeBonusAmount, "Welcome bonus", TransactionSource.WelcomeBonus, null);

        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateException) when (dbContext.Transactions.Any(
            t => t.EmployeeId == hired.EmployeeId && t.Source == TransactionSource.WelcomeBonus))
        {
            // Lost a race with another consumer instance - fine, already granted.
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
