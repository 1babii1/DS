namespace DirectoryService.Application.Database;

// Stages an outbox row on the ambient DbContext - it commits together with whatever
// SaveChanges call the handler makes next, in the same transaction as the domain write.
public interface IOutboxWriter
{
    void Enqueue(string type, string aggregateId, object payload);
}