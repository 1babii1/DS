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
    // Kafka's own broker default (7 days) - not a new policy, just making the value this
    // platform has always effectively run on explicit instead of implicit. A topic created
    // without retention.ms inherits whatever log.retention.hours the broker happens to be
    // configured with, which is an operational setting this codebase never actually sets or
    // documents; pinning it here means every topic keeps this value regardless of that.
    private const long DefaultRetentionMs = 604_800_000;

    public static async Task EnsureTopicsExistAsync(
        string bootstrapServers, KafkaSecurityOptions security, params string[] topics)
    {
        var config = new AdminClientConfig { BootstrapServers = bootstrapServers };
        security.ApplyTo(config);
        using var admin = new AdminClientBuilder(config).Build();

        try
        {
            await admin.CreateTopicsAsync(topics.Select(topic => SpecificationFor(topic, security)));
        }
        catch (CreateTopicsException ex) when (OnlyAlreadyExisted(ex.Results))
        {
            // Fine - another service instance created it first.
        }
    }

    /// <summary>
    /// What a topic is created with. With a replication factor above one, <c>min.insync.replicas</c> is what makes an
    /// acknowledged write mean "on several brokers": the leader refuses a write (rather than accept it alone) when fewer than
    /// that many replicas are in sync. It is only set when asked for, so a single broker is not given a rule it cannot meet.
    /// </summary>
    public static TopicSpecification SpecificationFor(string topic, KafkaSecurityOptions security)
    {
        var configs = new Dictionary<string, string> { ["retention.ms"] = DefaultRetentionMs.ToString() };
        if (security.TopicMinInsyncReplicas is { } minInsync)
        {
            configs["min.insync.replicas"] = minInsync.ToString();
        }

        return new TopicSpecification
        {
            Name = topic,
            NumPartitions = security.TopicPartitions,
            ReplicationFactor = (short)security.TopicReplicationFactor,
            Configs = configs,
        };
    }

    /// <summary>
    /// True when every topic that failed to be created failed because it was already there. Topics that were created in
    /// the same request report success, so a request for several topics, some of which exist, is still fine; the
    /// check used to demand that all of them existed, which looped forever on a mixed set.
    /// </summary>
    public static bool OnlyAlreadyExisted(IEnumerable<CreateTopicReport> results) =>
        results.Where(r => r.Error.IsError).All(r => r.Error.Code == ErrorCode.TopicAlreadyExists);

    /// <summary>
    /// Повторяет провижининг, пока брокер не ответит. Вызывается первой строкой
    /// ExecuteAsync у фоновых сервисов, а необработанное исключение оттуда по умолчанию
    /// останавливает весь хост (BackgroundServiceExceptionBehavior.StopHost). Без этого
    /// недоступная при старте Kafka уносила вместе с собой и HTTP-API сервиса, хотя
    /// шина нужна только для асинхронной доставки событий.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task WaitForTopicsAsync(
        string bootstrapServers,
        KafkaSecurityOptions security,
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
                await EnsureTopicsExistAsync(bootstrapServers, security, topics);
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