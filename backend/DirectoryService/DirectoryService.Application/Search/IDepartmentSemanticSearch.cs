using DirectoryService.Contracts.Response.Department;

namespace DirectoryService.Application.Search;

public interface IDepartmentSemanticSearch
{
    Task<List<DepartmentSearchResultDto>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}