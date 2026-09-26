using Pgvector;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

// Read-model-only projection, deliberately kept out of the Department aggregate:
// embeddings are a derived, best-effort search index, not a domain invariant.
public sealed class DepartmentEmbedding
{
    public Guid DepartmentId { get; set; }

    public Vector Embedding { get; set; } = null!;

    public DateTime UpdatedAt { get; set; }
}