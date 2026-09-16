using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using Shared.Outbox;

namespace SearchService.Web.Consumers;

// Structural copy of every other consumer in this codebase (retry, dead-letter,
// HandleWithRetryAndDeadLetter as the internal seam for direct-call tests). Subscribed to
// every producer topic in the platform - the "second/third/fourth/fifth independent
// consumer" story AuditConsumer's own comment describes, just for a search index instead
// of an audit log or a notification feed.
//
// Every message becomes an audit-kind document (EventType/SourceService/AggregateId/
// OccurredAt only - deliberately no Payload, so search never surfaces internal event
// bodies). Messages whose type matches one of the known entity events additionally
// upsert/delete the corresponding employee/department/position/location document.
public class DomainEventsConsumer(
    IServiceScopeFactory scopeFactory,
    SearchIndexClient indexClient,
    IOptions<DomainEventsConsumerOptions> options,
    ILogger<DomainEventsConsumer> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)];

    private readonly DomainEventsConsumerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaTopicProvisioner.WaitForTopicsAsync(
            _options.BootstrapServers, _options.Security, logger, stoppingToken, _options.Topics);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await indexClient.EnsureIndexAsync(stoppingToken);

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
                ProcessMessage(result).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(
                    ex,
                    "Failed to process search message on {Topic} (attempt {Attempt}/{MaxAttempts})",
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
            var dbContext = scope.ServiceProvider.GetRequiredService<SearchDbContext>();

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
                "Search message {MessageId} on {Topic} exhausted retries and was moved to dead_letters",
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
                "Failed to write dead letter for search message {MessageId} on {Topic} - the database is likely down",
                messageGuid,
                result.Topic);
            return false;
        }
    }

    private bool DeadLetterAlreadyRecorded(Guid messageGuid)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
        return dbContext.DeadLetters.Any(d => d.MessageId == messageGuid);
    }

    private async Task ProcessMessage(ConsumeResult<string, string> result)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
            return;
        }

        var sourceService = result.Topic.Replace(".events", string.Empty, StringComparison.Ordinal);
        var occurredAt = DateTime.UtcNow;

        // Every message is searchable as an audit-kind hit, regardless of whether it also
        // maps to a known entity kind below.
        await indexClient.UpsertAsync(
            new SearchDocument(
                messageGuid.ToString(),
                SearchKind.Audit,
                messageGuid,
                messageType ?? "Unknown",
                sourceService,
                $"{result.Message.Key} {messageType}",
                true,
                occurredAt),
            CancellationToken.None);

        switch (messageType)
        {
            case DepartmentCreatedEvent.MessageType:
                await HandleDepartmentCreated(result.Message.Value, occurredAt);
                break;
            case DepartmentDeletedEvent.MessageType:
                await HandleDepartmentDeleted(result.Message.Value);
                break;
            case PositionCreatedEvent.MessageType:
                await HandlePositionCreated(result.Message.Value, occurredAt);
                break;
            case LocationCreatedEvent.MessageType:
                await HandleLocationCreated(result.Message.Value, occurredAt);
                break;
            case EmployeeHiredEvent.MessageType:
                await HandleEmployeeHired(result.Message.Value, occurredAt);
                break;
            case EmployeeTransferredEvent.MessageType:
                await HandleEmployeeTransferred(result.Message.Value, occurredAt);
                break;
            case EmployeeTerminatedEvent.MessageType:
                await HandleEmployeeTerminated(result.Message.Value, occurredAt);
                break;
        }
    }

    private Task HandleDepartmentCreated(string payload, DateTime occurredAt)
    {
        var @event = JsonSerializer.Deserialize<DepartmentCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentCreatedEvent.MessageType} payload");

        return indexClient.UpsertAsync(
            new SearchDocument(
                SearchDocument.EntityId(SearchKind.Department, @event.DepartmentId),
                SearchKind.Department,
                @event.DepartmentId,
                @event.Name,
                @event.Identifier,
                $"{@event.Name} {@event.Identifier}",
                true,
                occurredAt),
            CancellationToken.None);
    }

    private Task HandleDepartmentDeleted(string payload)
    {
        var @event = JsonSerializer.Deserialize<DepartmentDeletedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentDeletedEvent.MessageType} payload");

        return indexClient.DeleteAsync(SearchDocument.EntityId(SearchKind.Department, @event.DepartmentId), CancellationToken.None);
    }

    private async Task HandlePositionCreated(string payload, DateTime occurredAt)
    {
        var @event = JsonSerializer.Deserialize<PositionCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {PositionCreatedEvent.MessageType} payload");

        var departmentNames = new List<string>();
        foreach (var departmentId in @event.DepartmentIds)
        {
            var department = await indexClient.GetAsync(
                SearchDocument.EntityId(SearchKind.Department, departmentId), CancellationToken.None);
            if (department is not null)
            {
                departmentNames.Add(department.Title);
            }
        }

        var subtitle = departmentNames.Count > 0 ? string.Join(", ", departmentNames) : @event.Description;

        await indexClient.UpsertAsync(
            new SearchDocument(
                SearchDocument.EntityId(SearchKind.Position, @event.PositionId),
                SearchKind.Position,
                @event.PositionId,
                @event.Name,
                subtitle,
                $"{@event.Name} {@event.Description} {string.Join(" ", departmentNames)}",
                true,
                occurredAt),
            CancellationToken.None);
    }

    private Task HandleLocationCreated(string payload, DateTime occurredAt)
    {
        var @event = JsonSerializer.Deserialize<LocationCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {LocationCreatedEvent.MessageType} payload");

        var subtitle = $"{@event.City}, {@event.Country} ({@event.Timezone})";

        return indexClient.UpsertAsync(
            new SearchDocument(
                SearchDocument.EntityId(SearchKind.Location, @event.LocationId),
                SearchKind.Location,
                @event.LocationId,
                @event.Name,
                subtitle,
                $"{@event.Name} {@event.Street} {@event.City} {@event.Country}",
                true,
                occurredAt),
            CancellationToken.None);
    }

    private async Task HandleEmployeeHired(string payload, DateTime occurredAt)
    {
        var @event = JsonSerializer.Deserialize<EmployeeHiredEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeHiredEvent.MessageType} payload");

        await UpsertEmployeeAsync(@event.EmployeeId, @event.FullName, @event.Email, @event.DepartmentId, @event.PositionId, true, occurredAt);
    }

    private async Task HandleEmployeeTransferred(string payload, DateTime occurredAt)
    {
        var @event = JsonSerializer.Deserialize<EmployeeTransferredEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeTransferredEvent.MessageType} payload");

        var existing = await indexClient.GetAsync(
            SearchDocument.EntityId(SearchKind.Employee, @event.EmployeeId), CancellationToken.None);

        // A transfer can outrace the hire (event ordering isn't guaranteed across topics) -
        // same documented caveat as every other cross-event lookup in this codebase
        // (ADR 0006). Nothing to update yet if the employee doc doesn't exist.
        if (existing is null)
        {
            return;
        }

        // SearchText for an employee doc is always "{fullName} {email}" (UpsertEmployeeAsync
        // below) - email is reliably the last token even when fullName itself has spaces.
        var title = existing.Title;
        var email = existing.SearchText.Split(' ').LastOrDefault() ?? string.Empty;

        await UpsertEmployeeAsync(@event.EmployeeId, title, email, @event.DepartmentId, @event.PositionId, true, occurredAt);
    }

    private async Task HandleEmployeeTerminated(string payload, DateTime occurredAt)
    {
        var @event = JsonSerializer.Deserialize<EmployeeTerminatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeTerminatedEvent.MessageType} payload");

        var existing = await indexClient.GetAsync(
            SearchDocument.EntityId(SearchKind.Employee, @event.EmployeeId), CancellationToken.None);
        if (existing is null)
        {
            return;
        }

        // Kept searchable, not removed - "active status" is required display metadata per
        // the issue, not a reason to hide the record.
        await indexClient.UpsertAsync(existing with { IsActive = false, OccurredAt = occurredAt }, CancellationToken.None);
    }

    private async Task UpsertEmployeeAsync(
        Guid employeeId, string fullName, string email, Guid departmentId, Guid positionId, bool isActive, DateTime occurredAt)
    {
        var department = await indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Department, departmentId), CancellationToken.None);
        var position = await indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Position, positionId), CancellationToken.None);

        var subtitle = $"{position?.Title} · {department?.Title}".Trim(' ', '·');

        await indexClient.UpsertAsync(
            new SearchDocument(
                SearchDocument.EntityId(SearchKind.Employee, employeeId),
                SearchKind.Employee,
                employeeId,
                fullName,
                subtitle,
                $"{fullName} {email}",
                isActive,
                occurredAt),
            CancellationToken.None);
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
