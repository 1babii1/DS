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
    DateTime OccurredAt,
    // What an employee or position document was built from, so a rename can find and rebuild the documents that carry
    // a department's name. Empty on documents indexed before these existed; those are refreshed by their next event.
    Guid[]? DepartmentIds = null,
    Guid? PositionId = null,
    string? Description = null)
{
    public static string EntityId(string kind, Guid sourceId) => $"{kind}:{sourceId}";
}
