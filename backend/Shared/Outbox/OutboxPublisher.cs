using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Avro;

namespace Shared.Outbox;

// Polling publisher: the simplest correct way to move rows written by a transaction
// into Kafka without a dual write. Delivery is at-least-once (a row is only marked
// processed after Kafka acknowledges the produce) - consumers must dedupe by message id.
public class OutboxPublisher<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxPublisherOptions> options,
    ILogger<OutboxPublisher<TContext>> logger,
    IEventAvroEncoder? avro = null) : BackgroundService
    where TContext : DbContext
{
    private readonly OutboxPublisherOptions _options = options.Value;
    private IProducer<string, byte[]>? _producer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Events go out only as Avro (ADR 0023): without a registry there is nothing this publisher can send.
        if (avro is not { IsConfigured: true })
        {
            throw new InvalidOperationException(
                "SchemaRegistry:Url must be set: events are published as Avro and need a schema registry.");
        }

        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, _options.Security, logger, stoppingToken,
            _options.Topic);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var producerConfig = new ProducerConfig { BootstrapServers = _options.BootstrapServers };
        _options.Security.ApplyTo(producerConfig);
        _producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

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
            .Where(m => m.ProcessedAt == null && m.ParkedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var result = await OutboxBatch.ProcessAsync(
            pending,
            (message, ct) => OutboxRowPublisher.PublishAsync(
                message,
                (m, bytes, token) => _producer!.ProduceAsync(_options.Topic, OutboxMessageHeaders.ToAvroKafkaMessage(m, bytes), token),
                avro!,
                ct),
            _options.MaxAttempts,
            logger,
            cancellationToken);

        // Failed messages carry updated attempt counts too, so save whenever anything changed.
        if (result.Succeeded > 0 || result.Failed > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Published {Succeeded}/{Total} outbox message(s) to {Topic}", result.Succeeded, pending.Count, _options.Topic);
    }

    public override void Dispose()
    {
        _producer?.Dispose();
        base.Dispose();
    }
}