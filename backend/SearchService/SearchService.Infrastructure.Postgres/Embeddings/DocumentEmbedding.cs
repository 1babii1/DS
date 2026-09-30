using Pgvector;

namespace SearchService.Infrastructure.Postgres.Embeddings;

// The semantic side of a search document. Written by the event consumer without any model call (Text, TextHash, and a
// null Embedding when the text is new or changed) and completed later by DocumentEmbeddingWorker. Everything the
// result list shows is kept here too, so a semantic hit needs no lookup elsewhere.
public sealed class DocumentEmbedding
{
    public string DocumentId { get; set; } = null!;

    public string Kind { get; set; } = null!;

    public Guid SourceId { get; set; }

    public string Title { get; set; } = null!;

    public string? Subtitle { get; set; }

    public bool IsActive { get; set; }

    public string Text { get; set; } = null!;

    public string TextHash { get; set; } = null!;

    public Vector? Embedding { get; set; }

    public DateTime UpdatedAt { get; set; }
}
