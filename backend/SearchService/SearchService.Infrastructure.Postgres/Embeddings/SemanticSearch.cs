using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace SearchService.Infrastructure.Postgres.Embeddings;

public sealed record SemanticHit(string Kind, Guid SourceId, string Title, string? Subtitle, double Distance);

// Nearest neighbours by cosine distance over the vectors the worker has made. Rows still waiting for a vector are
// simply not candidates. It covers the same set of documents as the keyword index (terminated employees stay,
// like there), so a comparison between the two modes is a comparison of ranking, not of what each can see.
public sealed class SemanticSearch(SearchDbContext db, IEmbeddingClient embeddingClient)
{
    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(
        string query,
        IReadOnlyCollection<string>? kinds,
        int limit,
        CancellationToken cancellationToken)
    {
        var queryVector = await embeddingClient.EmbedAsync(query, cancellationToken);

        var candidates = db.DocumentEmbeddings.Where(e => e.Embedding != null);
        if (kinds is { Count: > 0 })
        {
            candidates = candidates.Where(e => kinds.Contains(e.Kind));
        }

        // The LIMIT is what lets Postgres use the HNSW index for the ordering.
        var rows = await candidates
            .Select(e => new
            {
                e.Kind,
                e.SourceId,
                e.Title,
                e.Subtitle,
                Distance = e.Embedding!.CosineDistance(queryVector),
            })
            .OrderBy(x => x.Distance)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SemanticHit(r.Kind, r.SourceId, r.Title, r.Subtitle, r.Distance)).ToList();
    }
}
