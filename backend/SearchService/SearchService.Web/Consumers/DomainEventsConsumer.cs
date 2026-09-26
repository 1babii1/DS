using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using Shared.Kafka;

namespace SearchService.Web.Consumers;

// Subscribed to every producer topic in the platform - the "fifth independent consumer"
// story, just for a search index instead of an audit log or a notification feed. Consume
// loop, retries and dead-lettering come from KafkaRetryConsumer.
//
// Every message becomes an audit-kind document (EventType/SourceService/AggregateId/
// OccurredAt only - deliberately no Payload, so search never surfaces internal event
// bodies). Messages whose type matches one of the known entity events additionally
// upsert/delete the corresponding employee/department/position/location document.
public class DomainEventsConsumer(
    IServiceScopeFactory scopeFactory,
    SearchIndexClient indexClient,
    IOptions<DomainEventsConsumerOptions> options,
    ILogger<DomainEventsConsumer> logger)
    : KafkaRetryConsumer<SearchDbContext>(scopeFactory, options.Value, logger)
{
    protected override string MessageKind => "search";

    // The index has to exist before the first message is handled, not merely before the
    // first search: a consumed event upserts into it immediately.
    protected override Task OnStartingAsync(CancellationToken cancellationToken) =>
        indexClient.EnsureIndexAsync(cancellationToken);

    protected override async Task ProcessMessageAsync(
        ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        var messageId = GetHeader(result.Message.Headers, "message-id");
        var messageType = GetHeader(result.Message.Headers, "message-type");

        if (messageId is null || !Guid.TryParse(messageId, out var messageGuid))
        {
            Logger.LogWarning("Skipping message without a valid message-id header on topic {Topic}", result.Topic);
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
            cancellationToken);

        switch (messageType)
        {
            case DepartmentCreatedEvent.MessageType:
                await HandleDepartmentCreated(result.Message.Value, occurredAt, cancellationToken);
                break;
            case DepartmentDeletedEvent.MessageType:
                await HandleDepartmentDeleted(result.Message.Value, cancellationToken);
                break;
            case PositionCreatedEvent.MessageType:
                await HandlePositionCreated(result.Message.Value, occurredAt, cancellationToken);
                break;
            case LocationCreatedEvent.MessageType:
                await HandleLocationCreated(result.Message.Value, occurredAt, cancellationToken);
                break;
            case EmployeeHiredEvent.MessageType:
                await HandleEmployeeHired(result.Message.Value, occurredAt, cancellationToken);
                break;
            case EmployeeTransferredEvent.MessageType:
                await HandleEmployeeTransferred(result.Message.Value, occurredAt, cancellationToken);
                break;
            case EmployeeTerminatedEvent.MessageType:
                await HandleEmployeeTerminated(result.Message.Value, occurredAt, cancellationToken);
                break;
        }
    }

    private Task HandleDepartmentCreated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
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
            cancellationToken);
    }

    private Task HandleDepartmentDeleted(string payload, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<DepartmentDeletedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentDeletedEvent.MessageType} payload");

        return indexClient.DeleteAsync(SearchDocument.EntityId(SearchKind.Department, @event.DepartmentId), cancellationToken);
    }

    private async Task HandlePositionCreated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<PositionCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {PositionCreatedEvent.MessageType} payload");

        var departmentNames = new List<string>();
        foreach (var departmentId in @event.DepartmentIds)
        {
            var department = await indexClient.GetAsync(
                SearchDocument.EntityId(SearchKind.Department, departmentId), cancellationToken);
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
            cancellationToken);
    }

    private Task HandleLocationCreated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
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
            cancellationToken);
    }

    private async Task HandleEmployeeHired(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<EmployeeHiredEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeHiredEvent.MessageType} payload");

        await UpsertEmployeeAsync(@event.EmployeeId, @event.FullName, @event.Email, @event.DepartmentId, @event.PositionId, true, occurredAt, cancellationToken);
    }

    private async Task HandleEmployeeTransferred(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<EmployeeTransferredEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeTransferredEvent.MessageType} payload");

        var existing = await indexClient.GetAsync(
            SearchDocument.EntityId(SearchKind.Employee, @event.EmployeeId), cancellationToken);

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

        await UpsertEmployeeAsync(@event.EmployeeId, title, email, @event.DepartmentId, @event.PositionId, true, occurredAt, cancellationToken);
    }

    private async Task HandleEmployeeTerminated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<EmployeeTerminatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {EmployeeTerminatedEvent.MessageType} payload");

        var existing = await indexClient.GetAsync(
            SearchDocument.EntityId(SearchKind.Employee, @event.EmployeeId), cancellationToken);
        if (existing is null)
        {
            return;
        }

        // Kept searchable, not removed - "active status" is required display metadata per
        // the issue, not a reason to hide the record.
        await indexClient.UpsertAsync(existing with { IsActive = false, OccurredAt = occurredAt }, cancellationToken);
    }

    private async Task UpsertEmployeeAsync(
        Guid employeeId, string fullName, string email, Guid departmentId, Guid positionId, bool isActive, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var department = await indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Department, departmentId), cancellationToken);
        var position = await indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Position, positionId), cancellationToken);

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
            cancellationToken);
    }
}
