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
    private IProducer<string, string>? _producer;
    private IProducer<string, byte[]>? _avroProducer;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, _options.Security, logger, stoppingToken,
            AvroOn ? [_options.Topic, _options.AvroTopic!] : [_options.Topic]);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var producerConfig = new ProducerConfig { BootstrapServers = _options.BootstrapServers };
        _options.Security.ApplyTo(producerConfig);
        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
        if (AvroOn)
        {
            _avroProducer = new ProducerBuilder<string, byte[]>(producerConfig).Build();
        }

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
            .Where(OutboxRowPublisher.Pending(AvroOn))
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
                (m, token) => _producer!.ProduceAsync(_options.Topic, OutboxMessageHeaders.ToKafkaMessage(m), token),
                (m, bytes, token) => _avroProducer!.ProduceAsync(_options.AvroTopic, OutboxMessageHeaders.ToAvroKafkaMessage(m, bytes), token),
                avro,
                _options.AvroTopic,
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

    // Avro goes out only when a registry is configured and an Avro topic is named.
    private bool AvroOn => _options.AvroTopic is not null && avro is { IsConfigured: true };

    public override void Dispose()
    {
        _producer?.Dispose();
        _avroProducer?.Dispose();
        base.Dispose();
    }
}