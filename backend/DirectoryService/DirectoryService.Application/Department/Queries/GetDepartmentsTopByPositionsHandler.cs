using Dapper;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Response.Department;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Department.Queries;

public class GetDepartmentsTopByPositionsHandler
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<GetDepartmentsTopByPositionsHandler> _logger;
    private readonly HybridCache _cache;

    public GetDepartmentsTopByPositionsHandler(
        IDbConnectionFactory connectionFactory,
        ILogger<GetDepartmentsTopByPositionsHandler> logger, HybridCache cache)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _cache = cache;
    }

    public async Task<List<ReadDepartmentsTopDto>> Handle(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ищем в кэш");
        var department = await _cache.GetOrCreateAsync(
            key: GetKey.DepartmentKey.TopByPositions(),
            factory: async _ =>
            {
                _logger.LogInformation("В кеш не нашли идем в БД");
                return await GetDepartmentsTopByPositionsFromDb(cancellationToken);
            },
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return department;
    }

    private async Task<List<ReadDepartmentsTopDto>> GetDepartmentsTopByPositionsFromDb(
        CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var departments = await connection.QueryAsync<ReadDepartmentsTopDto>(
            """
            SELECT 
                d.id, 
                d.name, 
                d.parent_id, 
                d.created_at, 
                d.updated_at, 
                d.is_active, 
                d.identifier, 
                d.path, 
                d.depth,
                COUNT(dp.department_id) AS position_count
            FROM department_positions dp
            JOIN departments d ON dp.department_id = d.id
            GROUP BY 
                d.id, 
                d.name, 
                d.parent_id, 
                d.created_at, 
                d.updated_at, 
                d.is_active, 
                d.identifier, 
                d.path, 
                d.depth
            ORDER BY position_count DESC
            LIMIT 5
            """);

        return departments.ToList();
    }
}