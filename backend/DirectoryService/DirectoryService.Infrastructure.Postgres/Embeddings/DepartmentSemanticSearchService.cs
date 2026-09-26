using DirectoryService.Application.Search;
using DirectoryService.Contracts.Response.Department;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public sealed class DepartmentSemanticSearchService(
    DirectoryServiceDbContext db,
    IEmbeddingClient embeddingClient) : IDepartmentSemanticSearch
{
    // How many nearest neighbours to pull before filtering to active departments.
    // The HNSW index only accelerates a query Postgres can push a LIMIT into - an
    // unbounded ORDER BY forces a full scan regardless of the index, which is why
    // this multiplies the requested limit rather than fetching everything. Five times
    // over comfortably covers a handful of inactive departments landing in the nearest
    // neighbours; a search that returns fewer than the requested limit because
    // essentially every close match happens to be inactive is an acceptable, rare
    // trade-off against scanning the whole table on every request.
    private const int OverFetchFactor = 5;
    private const int MaxCandidates = 200;

    public async Task<List<DepartmentSearchResultDto>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var queryVector = await embeddingClient.EmbedAsync(query, cancellationToken);
        var candidateCount = Math.Min(limit * OverFetchFactor, MaxCandidates);

        // The LIMIT here is what lets Postgres use the HNSW index on embedding - without
        // it, ORDER BY distance has to produce a fully accurate order over every row,
        // which no ANN index accelerates.
        var ranked = await db.DepartmentEmbeddings
            .Select(e => new { e.DepartmentId, Distance = e.Embedding.CosineDistance(queryVector) })
            .OrderBy(x => x.Distance)
            .Take(candidateCount)
            .ToListAsync(cancellationToken);

        // Joining department_embeddings to departments in EF fails to translate here -
        // Department.Id goes through a value converter, and EF Core 10 can't build a
        // join/Contains predicate against it. A second, independent, directly
        // translatable query merged in memory sidesteps that entirely.
        var activeDepartments = await db.Departments
            .Where(d => d.IsActive)
            .Select(d => new { Id = d.Id.Value, Name = d.Name.Value, Identifier = d.Identifier.Value })
            .ToListAsync(cancellationToken);
        var byId = activeDepartments.ToDictionary(d => d.Id);

        return ranked
            .Where(r => byId.ContainsKey(r.DepartmentId))
            .Take(limit)
            .Select(r =>
            {
                var department = byId[r.DepartmentId];
                return new DepartmentSearchResultDto
                {
                    Id = department.Id,
                    Name = department.Name,
                    Identifier = department.Identifier,
                    Score = 1 - r.Distance,
                };
            })
            .ToList();
    }
}