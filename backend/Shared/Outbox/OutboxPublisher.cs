using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shared.Outbox;

// Polling publisher: the simplest correct way to move rows written by a transaction
// into Kafka without a dual write. Delivery is at-least-once (a row is only marked
// processed after Kafka acknowledges the produce) - consumers must dedupe by message id.
public class OutboxPublisher<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxPublisherOptions> options,
    ILogger<OutboxPublisher<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private readonly OutboxPublisherOptions _options = options.Value;
    private IProducer<string, string>? _producer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, logger, stoppingToken, _options.Topic);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _options.BootstrapServers }).Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox publish cycle failed for topic {Topic}", _options.Topic);
            }

            // Отмена при остановке - это штатное завершение, а не сбой: вылетевшее
            // отсюда исключение тоже остановило бы хост.
            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TContext>();

        var pending = await dbContext.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var message in pending)
        {
            var kafkaMessage = new Message<string, string>
            {
                Key = message.AggregateId,
                Value = message.Payload,
                Headers = new Headers
                {
                    { "message-id", System.Text.Encoding.UTF8.GetBytes(message.Id.ToString()) },
                    { "message-type", System.Text.Encoding.UTF8.GetBytes(message.Type) },
                },
            };

            await _producer!.ProduceAsync(_options.Topic, kafkaMessage, cancellationToken);
            message.MarkProcessed();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Published {Count} outbox message(s) to {Topic}", pending.Count, _options.Topic);
    }

    public override void Dispose()
    {
        _producer?.Dispose();
        base.Dispose();
    }
}