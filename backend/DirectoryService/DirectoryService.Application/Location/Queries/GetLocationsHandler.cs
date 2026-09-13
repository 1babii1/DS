using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;
using Shared;

namespace DirectoryService.Application.Location.Queries;

/// <summary>
/// Каталог локаций. В отличие от <see cref="GetLocationByDepartmentHandle"/> не ходит
/// через department_locations, поэтому локация, ещё ни к какому департаменту не
/// привязанная, тоже попадает в выдачу; departmentId работает как необязательный фильтр.
/// </summary>
public class GetLocationsHandler(IDbConnectionFactory connectionFactory)
{
    public async Task<PagedResponse<ReadLocationDto>> Handle(
        GetLocationsRequest request,
        CancellationToken cancellationToken)
    {
        var (page, size) = PagedResponse<ReadLocationDto>.Normalize(request.Page, request.Size);

        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (request.IsActive.HasValue)
        {
            conditions.Add("l.is_active = @isActive");
            parameters.Add("isActive", request.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            conditions.Add("l.name ILIKE '%' || @search || '%'");
            parameters.Add("search", request.Search.Trim());
        }

        if (request.DepartmentId.HasValue)
        {
            conditions.Add("""
                           EXISTS (SELECT 1 FROM directory.department_locations dl
                                   WHERE dl.location_id = l.id AND dl.department_id = @departmentId)
                           """);
            parameters.Add("departmentId", request.DepartmentId.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);

        var total = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT count(*) FROM directory.locations l {where}",
                parameters,
                cancellationToken: cancellationToken));

        if (total == 0)
        {
            return PagedResponse<ReadLocationDto>.Empty(page, size);
        }

        parameters.Add("limit", size);
        parameters.Add("offset", (page - 1) * size);

        var items = (await connection.QueryAsync<ReadLocationDto>(
            new CommandDefinition(
                $"""
                 SELECT l.id, l.name, l.timezone, l.street, l.city, l.country,
                        l.is_active, l.created_at, l.updated_at
                 FROM directory.locations l
                 {where}
                 ORDER BY l.is_active DESC, l.name, l.id
                 LIMIT @limit OFFSET @offset
                 """,
                parameters,
                cancellationToken: cancellationToken))).ToList();

        return new PagedResponse<ReadLocationDto>(items, page, size, total);
    }
}
