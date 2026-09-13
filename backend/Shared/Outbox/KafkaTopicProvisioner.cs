using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;

namespace Shared.Outbox;

// Kafka's auto-create-on-first-produce/subscribe is unreliable for this exact shape of
// problem: a consumer that subscribes before the topic exists falls back to a slow (default
// 5-minute) metadata refresh interval after a handful of fast retries, and can miss messages
// produced shortly after for a long time even once the topic exists. Explicit provisioning
// before anyone subscribes or produces avoids the whole class of race.
public static class KafkaTopicProvisioner
{
    public static async Task EnsureTopicsExistAsync(string bootstrapServers, params string[] topics)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        try
        {
            await admin.CreateTopicsAsync(topics.Select(topic => new TopicSpecification
            {
                Name = topic,
                NumPartitions = 1,
                ReplicationFactor = 1,
            }));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            // Fine - another service instance created it first.
        }
    }

    /// <summary>
    /// Повторяет провижининг, пока брокер не ответит. Вызывается первой строкой
    /// ExecuteAsync у фоновых сервисов, а необработанное исключение оттуда по умолчанию
    /// останавливает весь хост (BackgroundServiceExceptionBehavior.StopHost). Без этого
    /// недоступная при старте Kafka уносила вместе с собой и HTTP-API сервиса, хотя
    /// шина нужна только для асинхронной доставки событий.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    public static async Task WaitForTopicsAsync(
        string bootstrapServers,
        ILogger logger,
        CancellationToken cancellationToken,
        params string[] topics)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureTopicsExistAsync(bootstrapServers, topics);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Kafka at {BootstrapServers} is not reachable; retrying topic provisioning in {Delay}",
                    bootstrapServers,
                    delay);
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }
    }
}