using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using SearchService.Infrastructure.Postgres.Embeddings;
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
    ILogger<DomainEventsConsumer> logger,
    Shared.Avro.IEventAvroDecoder? avro = null)
    : KafkaRetryConsumer<SearchDbContext>(scopeFactory, options.Value, logger, avro)
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

        // A subject whose data was erased (ADR 0047) is not indexed again, by a late or a replayed event either.
        using (var erasedScope = ScopeFactory.CreateScope())
        {
            if (await erasedScope.ServiceProvider.GetRequiredService<SubjectErasure>().IsErasedAsync(result.Message.Key, cancellationToken))
            {
                return;
            }
        }

        // "directory.events.v2" and "directory.events" are the same source.
        var sourceService = result.Topic.Replace(".v2", string.Empty, StringComparison.Ordinal)
            .Replace(".events", string.Empty, StringComparison.Ordinal);
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
            case DepartmentRenamedEvent.MessageType:
                await HandleDepartmentRenamed(result.Message.Value, occurredAt, cancellationToken);
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

    // Every entity write goes to the keyword index and, next to it, is staged for the semantic one. Audit documents are
    // not entities and are not embedded. Staging records the text only; the vector is made later by a worker, so this
    // never waits on the embedding model.
    private async Task IndexEntityAsync(SearchDocument document, CancellationToken cancellationToken)
    {
        await indexClient.UpsertAsync(document, cancellationToken);

        using var scope = ScopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EmbeddingStaging>().StageAsync(document, cancellationToken);
    }

    private async Task DeleteEntityAsync(string documentId, CancellationToken cancellationToken)
    {
        await indexClient.DeleteAsync(documentId, cancellationToken);

        using var scope = ScopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EmbeddingStaging>().RemoveAsync(documentId, cancellationToken);
    }

    private Task HandleDepartmentCreated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<DepartmentCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentCreatedEvent.MessageType} payload");

        return IndexEntityAsync(
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

    // The event carries the whole set of fields the document is built from, so the document is rebuilt and
    // overwritten (the same idempotent full re-index as on creation). Only active departments can be renamed at the
    // source, so the document stays active.
    //
    // Employee and position documents carry the department's name in their own text, so they are rebuilt from the
    // documents they were made from. That reads current state rather than comparing old and new names, which makes it
    // safe to repeat when the message is redelivered after a partial failure.
    private async Task HandleDepartmentRenamed(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<DepartmentRenamedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentRenamedEvent.MessageType} payload");

        await IndexEntityAsync(
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

        foreach (var dependent in await indexClient.FindByDepartmentAsync(@event.DepartmentId, cancellationToken))
        {
            var rebuilt = dependent.Kind switch
            {
                SearchKind.Employee => await RebuildEmployeeAsync(dependent, occurredAt, cancellationToken),
                SearchKind.Position => await RebuildPositionAsync(dependent, occurredAt, cancellationToken),
                _ => null,
            };
            if (rebuilt is not null)
            {
                await IndexEntityAsync(rebuilt, cancellationToken);
            }
        }
    }

    private async Task<SearchDocument?> RebuildEmployeeAsync(SearchDocument existing, DateTime occurredAt, CancellationToken cancellationToken)
    {
        if (existing.DepartmentIds is not [var departmentId] || existing.PositionId is not { } positionId)
        {
            return null;
        }

        // SearchText for an employee doc is always "{fullName} {email}" - email is the last token.
        var email = existing.SearchText.Split(' ').LastOrDefault() ?? string.Empty;
        return await BuildEmployeeAsync(
            existing.SourceId, existing.Title, email, departmentId, positionId, existing.IsActive, occurredAt, cancellationToken);
    }

    private async Task<SearchDocument?> RebuildPositionAsync(SearchDocument existing, DateTime occurredAt, CancellationToken cancellationToken)
    {
        if (existing.DepartmentIds is not { Length: > 0 } departmentIds)
        {
            return null;
        }

        return await BuildPositionAsync(
            existing.SourceId, existing.Title, existing.Description, departmentIds, occurredAt, cancellationToken);
    }

    private Task HandleDepartmentDeleted(string payload, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<DepartmentDeletedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {DepartmentDeletedEvent.MessageType} payload");

        return DeleteEntityAsync(SearchDocument.EntityId(SearchKind.Department, @event.DepartmentId), cancellationToken);
    }

    private async Task HandlePositionCreated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<PositionCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {PositionCreatedEvent.MessageType} payload");

        await IndexEntityAsync(
            await BuildPositionAsync(
                @event.PositionId, @event.Name, @event.Description, @event.DepartmentIds, occurredAt, cancellationToken),
            cancellationToken);
    }

    private async Task<SearchDocument> BuildPositionAsync(
        Guid positionId, string name, string? description, Guid[] departmentIds, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var departmentNames = new List<string>();
        foreach (var departmentId in departmentIds)
        {
            var department = await indexClient.GetAsync(
                SearchDocument.EntityId(SearchKind.Department, departmentId), cancellationToken);
            if (department is not null)
            {
                departmentNames.Add(department.Title);
            }
        }

        var subtitle = departmentNames.Count > 0 ? string.Join(", ", departmentNames) : description;

        return new SearchDocument(
            SearchDocument.EntityId(SearchKind.Position, positionId),
            SearchKind.Position,
            positionId,
            name,
            subtitle,
            $"{name} {description} {string.Join(" ", departmentNames)}",
            true,
            occurredAt,
            departmentIds,
            Description: description);
    }

    private Task HandleLocationCreated(string payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var @event = JsonSerializer.Deserialize<LocationCreatedEvent>(payload)
            ?? throw new InvalidOperationException($"Could not deserialize {LocationCreatedEvent.MessageType} payload");

        var subtitle = $"{@event.City}, {@event.Country} ({@event.Timezone})";

        return IndexEntityAsync(
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
        await IndexEntityAsync(existing with { IsActive = false, OccurredAt = occurredAt }, cancellationToken);
    }

    private async Task UpsertEmployeeAsync(
        Guid employeeId, string fullName, string email, Guid departmentId, Guid positionId, bool isActive, DateTime occurredAt, CancellationToken cancellationToken) =>
        await IndexEntityAsync(
            await BuildEmployeeAsync(employeeId, fullName, email, departmentId, positionId, isActive, occurredAt, cancellationToken),
            cancellationToken);

    private async Task<SearchDocument> BuildEmployeeAsync(
        Guid employeeId, string fullName, string email, Guid departmentId, Guid positionId, bool isActive, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var department = await indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Department, departmentId), cancellationToken);
        var position = await indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Position, positionId), cancellationToken);

        var subtitle = $"{position?.Title} · {department?.Title}".Trim(' ', '·');

        return new SearchDocument(
            SearchDocument.EntityId(SearchKind.Employee, employeeId),
            SearchKind.Employee,
            employeeId,
            fullName,
            subtitle,
            $"{fullName} {email}",
            isActive,
            occurredAt,
            [departmentId],
            positionId);
    }
}
