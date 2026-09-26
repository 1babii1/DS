using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;
using Shared;

namespace DirectoryService.Application.Location.Queries;

public class GetLocationByDepartmentHandle
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public GetLocationByDepartmentHandle(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    public async Task<List<ReadLocationDto>?> Handle(
        GetLocationByDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        using var connection = await _dbConnectionFactory.CreateConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            conditions.Add("l.name ILIKE '%' || @search || '%'");
            parameters.Add("search", request.Search);
        }

        if (request.IsActive.HasValue)
        {
            conditions.Add("l.is_active = @isActive");
            parameters.Add("isActive", request.IsActive.Value);
        }

        if (request.DepartmentId != null)
        {
            conditions.Add("dl.department_id = ANY(@DepartmentId::uuid[]) ");
            parameters.Add("DepartmentId", request.DepartmentId);
        }

        // Straight through Normalize, like every other catalogue query: PageSize reached
        // SQL's LIMIT unvalidated, so an omitted value became "LIMIT NULL" (no limit at
        // all in Postgres) and a large one dumped the whole join in a single response.
        var (page, pageSize) = PagedResponse<ReadLocationDto>.Normalize(request.Page, request.PageSize);

        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        var departmentLocationDto = await connection.QueryAsync<ReadLocationDto>(
            new CommandDefinition(
                $"""
                 SELECT l.id, l.name, l.timezone, l.street, l.city, l.country, l.is_active, l.created_at, l.updated_at FROM department_locations dl
                 JOIN locations l ON dl.location_id = l.id
                 {whereClause}
                 ORDER BY l.is_active, l.name
                 LIMIT @limit OFFSET @offset
                 """,
                parameters,
                cancellationToken: cancellationToken));

        return departmentLocationDto.ToList();
    }
}