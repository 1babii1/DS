using DirectoryService.Application.Search;
using DirectoryService.Contracts.Response.Department;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DirectoryService.Infrastructure.Postgres.Embeddings;

public sealed class DepartmentSemanticSearchService(
    DirectoryServiceDbContext db,
    IEmbeddingClient embeddingClient) : IDepartmentSemanticSearch
{
    public async Task<List<DepartmentSearchResultDto>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var queryVector = await embeddingClient.EmbedAsync(query, cancellationToken);

        // Joining department_embeddings to departments in EF fails to translate here -
        // Departments.Id goes through a value converter, and EF Core 10 can't build a
        // join/Contains predicate against it. Two independent, single-table queries
        // (both directly translatable) merged in memory sidesteps that entirely; fine
        // at this project's scale.
        var ranked = await db.DepartmentEmbeddings
            .Select(e => new { e.DepartmentId, Distance = e.Embedding.CosineDistance(queryVector) })
            .OrderBy(x => x.Distance)
            .ToListAsync(cancellationToken);

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
