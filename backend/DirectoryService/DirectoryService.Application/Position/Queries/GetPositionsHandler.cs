using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Position;
using DirectoryService.Contracts.Response.Position;
using Shared;

namespace DirectoryService.Application.Position.Queries;

/// <summary>
/// Каталог позиций вместе с департаментами, к которым они привязаны.
/// Dapper, а не EF: сортировка и поиск идут по полям, которые в модели спрятаны за
/// value-объектами, и EF не умеет транслировать обращение к их .Value.
/// </summary>
public class GetPositionsHandler(IDbConnectionFactory connectionFactory)
{
    public async Task<PagedResponse<ReadPositionDto>> Handle(
        GetPositionsRequest request,
        CancellationToken cancellationToken)
    {
        var (page, size) = PagedResponse<ReadPositionDto>.Normalize(request.Page, request.Size);

        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (request.IsActive.HasValue)
        {
            conditions.Add("p.is_active = @isActive");
            parameters.Add("isActive", request.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            conditions.Add("p.name ILIKE '%' || @search || '%'");
            parameters.Add("search", request.Search.Trim());
        }

        if (request.DepartmentId.HasValue)
        {
            conditions.Add("""
                           EXISTS (SELECT 1 FROM directory.department_positions dp
                                   WHERE dp.position_id = p.id AND dp.department_id = @departmentId)
                           """);
            parameters.Add("departmentId", request.DepartmentId.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        var connection = await connectionFactory.CreateConnectionAsync(cancellationToken);

        var total = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT count(*) FROM directory.positions p {where}",
                parameters,
                cancellationToken: cancellationToken));

        if (total == 0)
        {
            return PagedResponse<ReadPositionDto>.Empty(page, size);
        }

        parameters.Add("limit", size);
        parameters.Add("offset", (page - 1) * size);

        // Сортировка включает id последним ключом: без него позиции с одинаковым именем
        // могут разъезжаться между страницами.
        var rows = (await connection.QueryAsync<PositionRow>(
            new CommandDefinition(
                $"""
                 SELECT p.id, p.name, p.description, p.is_active, p.created_at, p.updated_at
                 FROM directory.positions p
                 {where}
                 ORDER BY p.is_active DESC, p.name, p.id
                 LIMIT @limit OFFSET @offset
                 """,
                parameters,
                cancellationToken: cancellationToken))).ToList();

        var departments = await LoadDepartments(connection, rows.Select(r => r.Id).ToArray(), cancellationToken);

        var items = rows
            .Select(r => new ReadPositionDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                IsActive = r.IsActive,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
                Departments = departments.TryGetValue(r.Id, out var linked) ? linked : [],
            })
            .ToList();

        return new PagedResponse<ReadPositionDto>(items, page, size, total);
    }

    private static async Task<Dictionary<Guid, List<PositionDepartmentDto>>> LoadDepartments(
        System.Data.IDbConnection connection,
        Guid[] positionIds,
        CancellationToken cancellationToken)
    {
        var links = await connection.QueryAsync<DepartmentLinkRow>(
            new CommandDefinition(
                """
                SELECT dp.position_id, d.id, d.name, d.identifier
                FROM directory.department_positions dp
                JOIN directory.departments d ON d.id = dp.department_id
                WHERE dp.position_id = ANY(@positionIds)
                ORDER BY d.name
                """,
                new { positionIds },
                cancellationToken: cancellationToken));

        return links
            .GroupBy(l => l.PositionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(l => new PositionDepartmentDto(l.Id, l.Name, l.Identifier)).ToList());
    }

    private sealed record PositionRow(
        Guid Id,
        string Name,
        string? Description,
        bool IsActive,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    private sealed record DepartmentLinkRow(Guid PositionId, Guid Id, string Name, string Identifier);
}
