using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Contracts.Response.Department;
using Shared;

namespace DirectoryService.Application.Department.Queries;

/// <summary>
/// Каталог департаментов, отфильтрованный по локациям. Dapper, а не EF: locationIds
/// нужно сравнивать с LocationId в DepartmentsLocationsList, а это value object за EF
/// value-конвертером - Contains() над такой коллекцией не транслируется в SQL и падал
/// с 500 на каждый запрос с непустым locationIds.
/// Фильтр необязательный, как и в зеркальном <see cref="Location.Queries.GetLocationByDepartmentHandle"/>:
/// без locationIds возвращается весь каталог.
/// </summary>
public class GetDepartmentByLocationHandler(IDbConnectionFactory connectionFactory)
{
    public async Task<List<ReadDepartmentDto>?> Handle(
        GetDepartmentByLocationRequest request,
        CancellationToken cancellationToken)
    {
        using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (request.LocationIds is { Length: > 0 })
        {
            conditions.Add("""
                           EXISTS (SELECT 1 FROM directory.department_locations dl
                                   WHERE dl.department_id = d.id AND dl.location_id = ANY(@locationIds::uuid[]))
                           """);
            parameters.Add("locationIds", request.LocationIds);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            conditions.Add("d.name ILIKE '%' || @search || '%'");
            parameters.Add("search", request.Search.Trim());
        }

        if (request.IsActive.HasValue)
        {
            conditions.Add("d.is_active = @isActive");
            parameters.Add("isActive", request.IsActive.Value);
        }

        // Normalize rather than a hand-rolled default: the previous check only guarded the
        // lower bound, leaving PageSize with no ceiling at all before it reached SQL's LIMIT.
        var (page, pageSize) = PagedResponse<ReadDepartmentDto>.Normalize(request.Page, request.PageSize);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        var departments = await connection.QueryAsync<ReadDepartmentDto>(
            new CommandDefinition(
                $"""
                 SELECT d.id, d.parent_id, d.name, d.identifier, d.path, d.depth,
                        d.is_active, d.created_at, d.updated_at
                 FROM directory.departments d
                 {whereClause}
                 ORDER BY d.is_active DESC, d.name, d.id
                 LIMIT @limit OFFSET @offset
                 """,
                parameters,
                cancellationToken: cancellationToken));

        return departments.ToList();
    }
}