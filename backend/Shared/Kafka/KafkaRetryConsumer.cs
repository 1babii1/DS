using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.Avro;
using Shared.Outbox;

namespace Shared.Kafka;

/// <summary>
/// The consume/retry/dead-letter machinery every Kafka consumer in this platform runs,
/// with only the per-message handling left to the service.
/// <para>
/// This existed six times as a near-identical copy. That duplication was not free: a fix
/// applied to one copy did not reach the others, which is how database calls in one
/// consumer kept passing <c>CancellationToken.None</c> long after the same bug had been
/// fixed elsewhere. Event <em>contracts</em> stay deliberately per-service - a consumer
/// owning its own expectation of a payload is the point - but this is mechanism, and
/// mechanism belongs in one place.
/// </para>
/// </summary>
/// <typeparam name="TDbContext">
/// The service's own DbContext. Only used to reach its <see cref="DeadLetterEntry"/> set,
/// which each service maps into its own schema.
/// </typeparam>
public abstract class KafkaRetryConsumer<TDbContext> : BackgroundService
    where TDbContext : DbContext, IHasDeadLetters
{
    /// <summary>
    /// Bounded retries in place, before the offset is committed: Consume() always moves
    /// forward regardless of commit, so retrying only works by not fetching the next
    /// message until this one is dealt with. Enough attempts to ride out a brief Postgres
    /// reconnect, not so many that a real outage stalls the whole topic for minutes.
    /// </summary>
    protected const int MaxAttempts = 3;

    /// <summary>Added to the headers a handler sees when the message was Avro: the id of the schema it was written with.</summary>
    public const string AvroSchemaIdHeader = "avro-schema-id";

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    private readonly IEventAvroDecoder? _avro;

    protected KafkaRetryConsumer(
        IServiceScopeFactory scopeFactory,
        KafkaConsumerOptions options,
        ILogger logger,
        IEventAvroDecoder? avro = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        Options = options;
        _avro = avro;
    }

    protected KafkaConsumerOptions Options { get; }

    protected IServiceScopeFactory ScopeFactory => _scopeFactory;

    protected ILogger Logger => _logger;

    /// <summary>Names this consumer in log messages, e.g. "audit" or "search".</summary>
    protected abstract string MessageKind { get; }

    /// <summary>
    /// Handles one message. Throwing is how a message asks to be retried, and ultimately
    /// dead-lettered - it is the documented failure signal, not an accident.
    /// </summary>
    protected abstract Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken);

    /// <summary>
    /// Runs once after the topics exist and before the consume loop starts, for setup that
    /// must not race the first message (SearchService creating its index, for instance).
    /// </summary>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <returns>
    /// True once the message is durably accounted for - either handled normally or parked
    /// in dead_letters - and it is safe to commit its offset.
    /// </returns>
    /// <remarks>
    /// Public, not private: this is the entire retry/dead-letter decision and it needs no
    /// live broker (ConsumeResult is just a data holder here), so integration tests call it
    /// directly against a real Postgres rather than standing up a Kafka test harness. It
    /// was <c>internal</c> plus <c>[InternalsVisibleTo]</c> while each service owned a copy;
    /// sharing the base class means the seam has to cross assemblies.
    /// </remarks>
    public bool HandleWithRetryAndDeadLetter(
        ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                ProcessMessageAsync(result, stoppingToken).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "Failed to process {MessageKind} message on {Topic} (attempt {Attempt}/{MaxAttempts})",
                    MessageKind,
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

    protected static string? GetHeader(Headers headers, string key) =>
        headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            Options.BootstrapServers, Options.Security, _logger, stoppingToken, Options.Topics);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await OnStartingAsync(stoppingToken);

        await Task.Run(() => Run(stoppingToken), stoppingToken);
    }

    private void Run(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = Options.BootstrapServers,
            GroupId = Options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        Options.Security.ApplyTo(config);

        // The value is read as bytes so that one consumer can read a JSON topic and an Avro topic alike (ADR 0023): a message
        // whose first byte is zero is Avro in the Confluent wire format (JSON never starts with it) and is turned back into
        // the JSON the handlers have always been given.
        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(Options.Topics);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, byte[]>? raw;
            try
            {
                raw = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
                Thread.Sleep(TimeSpan.FromSeconds(1));
                continue;
            }

            if (raw?.Message is null)
            {
                continue;
            }

            ConsumeResult<string, string> result;
            try
            {
                result = AsText(raw, out var undecodable);
                if (undecodable is not null)
                {
                    // Nothing a retry can fix: park it with its bytes and move on.
                    if (TryDeadLetter(result, undecodable))
                    {
                        consumer.Commit(raw);
                        continue;
                    }

                    throw undecodable;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The registry could not be asked (down, slow): the message is not lost, the consumer stalls on it
                // and asks again, the same way it waits for a database that is down.
                _logger.LogError(ex, "Could not decode a message on {Topic}; will try again", raw.Topic);
                consumer.Seek(raw.TopicPartitionOffset);
                try
                {
                    Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (HandleWithRetryAndDeadLetter(result, stoppingToken))
            {
                consumer.Commit(raw);
            }
            else
            {
                // Both retries and the dead-letter write failed - almost certainly the
                // database itself is down. Seeking back means the next Consume() returns
                // this exact message again instead of silently skipping past it, so the
                // consumer stalls here rather than losing the record. Nothing committed so
                // far is lost: earlier offsets are already committed, and this message gets
                // replayed - safely, since every handler here is idempotent by message id -
                // once the database recovers.
                consumer.Seek(raw.TopicPartitionOffset);

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

    /// <summary>
    /// The message as the handlers see it. JSON bytes become a string. An Avro message is decoded; when that cannot ever
    /// succeed (not Avro after all, a corrupt body, a schema id the registry does not know: an answer, not an outage) <paramref name="undecodable"/> says why and the
    /// value is the raw bytes in base64, so the dead letter keeps them. Registry or network failures are not caught here:
    /// they are for the caller to wait out.
    /// </summary>
    public ConsumeResult<string, string> AsText(ConsumeResult<string, byte[]> raw, out Exception? undecodable)
    {
        undecodable = null;
        var bytes = raw.Message.Value ?? [];
        string value;
        var headers = new Headers();
        foreach (var header in raw.Message.Headers)
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        if (bytes.Length > 0 && bytes[0] == 0)
        {
            try
            {
                var decoder = _avro
                    ?? throw new InvalidOperationException("An Avro message arrived but no schema registry is configured for this consumer");
                var decoded = decoder.Decode(bytes, GetHeader(raw.Message.Headers, "message-type"));
                value = decoded.Json;

                // Which writer schema the event was written with, for a consumer that records it (AuditService).
                headers.Add(AvroSchemaIdHeader, Encoding.UTF8.GetBytes(decoded.SchemaId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is FormatException or global::Avro.AvroException or IndexOutOfRangeException or EndOfStreamException
                or Confluent.SchemaRegistry.SchemaRegistryException { Status: System.Net.HttpStatusCode.NotFound })
            {
                undecodable = ex;
                value = "avro-undecodable:" + Convert.ToBase64String(bytes);
            }
        }
        else
        {
            value = Encoding.UTF8.GetString(bytes);
        }

        return new ConsumeResult<string, string>
        {
            Topic = raw.Topic,
            Partition = raw.Partition,
            Offset = raw.Offset,
            IsPartitionEOF = raw.IsPartitionEOF,
            Message = new Message<string, string>
            {
                Key = raw.Message.Key,
                Value = value,
                Headers = headers,
                Timestamp = raw.Message.Timestamp,
            },
        };
    }

    private bool TryDeadLetter(ConsumeResult<string, string> result, Exception error)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            // No id to key the dead letter on either - already logged while processing.
            // There is nothing further to retry for a message shaped like this.
            return true;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();

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

            _logger.LogError(
                error,
                "{MessageKind} message {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
                MessageKind,
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
            _logger.LogCritical(
                ex,
                "Failed to write dead letter for {MessageKind} message {MessageId} on {Topic} - the database is likely down",
                MessageKind,
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return dbContext.DeadLetters.Any(d => d.MessageId == messageGuid);
    }
}
