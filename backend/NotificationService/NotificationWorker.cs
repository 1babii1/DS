using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Outbox;

namespace NotificationService;

// Independent consumer group on the same topics AuditService reads - proves the event
// backbone is a real fan-out, not wiring built for a single consumer. No persistence:
// a missed or duplicate "notification" here is harmless (unlike audit, which must be
// a complete record), so there's no dedupe/offset-commit ceremony beyond Kafka's own.
public class NotificationWorker(IOptions<NotificationOptions> options, ILogger<NotificationWorker> logger)
    : BackgroundService
{
    private readonly NotificationOptions _options = options.Value;

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
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.Topics);

        logger.LogInformation("NotificationService listening on topics: {Topics}", string.Join(", ", _options.Topics));

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

            var messageType = GetHeader(result.Message.Headers, "message-type") ?? "Unknown";
            logger.LogInformation(
                "Notification sent for {EventType} (aggregate {AggregateId}): {Payload}",
                messageType,
                result.Message.Key,
                result.Message.Value);
        }

        consumer.Close();
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

public class NotificationOptions
{
    public string BootstrapServers { get; set; } = null!;

    public string[] Topics { get; set; } = [];

    public string GroupId { get; set; } = "notification-service";
}
