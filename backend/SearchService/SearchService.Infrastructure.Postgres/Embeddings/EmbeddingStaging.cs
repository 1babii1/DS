using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SearchService.Domain;

namespace SearchService.Infrastructure.Postgres.Embeddings;

// Called by the event consumer next to every write of an entity to the keyword index (audit documents do not pass
// through here, so they are never embedded). It only records what to embed: the vector
// comes later from the worker, so the consumer never waits on (or fails because of) the model. A row keeps its
// vector while its text is unchanged, so redelivering an event does not throw away work already done.
public sealed class EmbeddingStaging(SearchDbContext db)
{
    public async Task StageAsync(SearchDocument document, CancellationToken cancellationToken)
    {
        var text = EmbeddingText.For(document);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        var row = await db.DocumentEmbeddings.SingleOrDefaultAsync(e => e.DocumentId == document.Id, cancellationToken);
        if (row is null)
        {
            row = new DocumentEmbedding { DocumentId = document.Id, Kind = document.Kind, SourceId = document.SourceId };
            db.DocumentEmbeddings.Add(row);
        }

        if (row.TextHash != hash)
        {
            row.Embedding = null;
        }

        row.Title = document.Title;
        row.Subtitle = document.Subtitle;
        row.IsActive = document.IsActive;
        row.Text = text;
        row.TextHash = hash;
        row.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task RemoveAsync(string documentId, CancellationToken cancellationToken) =>
        db.DocumentEmbeddings.Where(e => e.DocumentId == documentId).ExecuteDeleteAsync(cancellationToken);
}
