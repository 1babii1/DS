namespace SearchService.Domain;

// One shape for every kind, backing one Elasticsearch index (mapping documented in
// SearchService.Infrastructure.Elasticsearch) - a plain DTO, not an aggregate with
// invariants, since nothing here is ever mutated in place: every write from
// DomainEventsConsumer is a full re-index of the document.
//
// Id is the Elasticsearch document _id, not a random Guid: "{kind}:{sourceId}" for entity
// documents (employee/department/position/location) and the Kafka message-id for audit
// documents. Indexing twice with the same Id is idempotent by construction - Elasticsearch
// just overwrites, which is what makes redelivery safe without a separate "already
// processed" check.
public record SearchDocument(
    string Id,
    string Kind,
    Guid SourceId,
    string Title,
    string? Subtitle,
    string SearchText,
    bool IsActive,
    DateTime OccurredAt)
{
    public static string EntityId(string kind, Guid sourceId) => $"{kind}:{sourceId}";
}
