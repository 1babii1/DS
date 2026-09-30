using Pgvector;

namespace SearchService.Infrastructure.Postgres.Embeddings;

public interface IEmbeddingClient
{
    Task<Vector> EmbedAsync(string text, CancellationToken cancellationToken);
}
