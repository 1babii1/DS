using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;

namespace DirectoryService.Application.Location.Queries;

public class GetLocationByDepartmentHandle
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public GetLocationByDepartmentHandle(IDbConnectionFactory _dbConnectionFactory)
    {
        this._dbConnectionFactory = _dbConnectionFactory;
    }

    public async Task<List<ReadLocationDto>?> Handle(
        GetLocationByDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        var connection = await _dbConnectionFactory.CreateConnectionAsync(cancellationToken);

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

        parameters.Add("limit", request.PageSize);
        parameters.Add("offset", (request.Page - 1) * request.PageSize);

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        Console.WriteLine(request);
        var departmentLocationDto = await connection.QueryAsync<ReadLocationDto>(
            $"""
            SELECT l.id, l.name, l.timezone, l.street, l.city, l.country, l.is_active, l.created_at, l.updated_at FROM department_locations dl
            JOIN locations l ON dl.location_id = l.id
            {whereClause}
            ORDER BY l.is_active, l.name
            LIMIT @limit OFFSET @offset                                                                                                                  
            """,
            parameters);

        return departmentLocationDto.ToList();
    }
}