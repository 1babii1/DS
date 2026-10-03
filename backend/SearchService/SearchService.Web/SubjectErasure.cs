using Microsoft.EntityFrameworkCore;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using SearchService.Infrastructure.Postgres.Embeddings;

namespace SearchService.Web;

public sealed record ErasureResult(int EmployeeDocuments, long AuditDocuments);

/// <summary>
/// Removes what the search index holds about a person (ADR 0047): their employee document and its staged embedding, and the audit-kind
/// documents that carry their identifier (for a failed sign-in, that is the address tried), and leaves a mark so that no later or replayed
/// event indexes them again.
/// </summary>
public sealed class SubjectErasure(SearchDbContext db, SearchIndexClient index, EmbeddingStaging staging)
{
    public async Task<bool> IsErasedAsync(string subjectId, CancellationToken cancellationToken) =>
        await db.ErasedSubjects.AsNoTracking().AnyAsync(e => e.SubjectId == subjectId, cancellationToken);

    public async Task<ErasureResult> EraseAsync(IEnumerable<string> subjects, CancellationToken cancellationToken)
    {
        var employees = 0;
        long audit = 0;
        foreach (var subject in subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO search.erased_subjects ("SubjectId", "ErasedAt") VALUES ({subject}, now())
                ON CONFLICT ("SubjectId") DO NOTHING
                """, cancellationToken);

            if (Guid.TryParse(subject, out var id))
            {
                var documentId = SearchDocument.EntityId(SearchKind.Employee, id);
                if (await index.GetAsync(documentId, cancellationToken) is not null)
                {
                    await index.DeleteAsync(documentId, cancellationToken);
                    employees++;
                }

                await staging.RemoveAsync(documentId, cancellationToken);
            }

            audit += await index.DeleteAuditDocumentsMentioningAsync(subject, cancellationToken);
        }

        return new ErasureResult(employees, audit);
    }
}
