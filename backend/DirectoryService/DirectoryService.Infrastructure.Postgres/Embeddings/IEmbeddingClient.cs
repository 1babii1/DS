using Pgvector;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public interface IEmbeddingClient
{
    Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken);
}
