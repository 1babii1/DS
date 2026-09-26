using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;

namespace DirectoryService.Application.Location.Queries;

public class GetLocationByIdHandle
{
    private readonly IDbConnectionFactory _dbConnectionFactory;

    public GetLocationByIdHandle(IDbConnectionFactory dbConnectionFactory)
    {
        _dbConnectionFactory = dbConnectionFactory;
    }

    public async Task<ReadLocationDto?> Handle(GetLocationByIdRequest request, CancellationToken cancellationToken)
    {
        using var connection = await _dbConnectionFactory.CreateConnectionAsync(cancellationToken);

        var locationDto = await connection.QueryAsync<ReadLocationDto>(
            new CommandDefinition(
                """
                SELECT l.id, l.name, l.timezone, l.street, l.city, l.country, l.is_active, l.created_at, l.updated_at FROM locations l
                WHERE l.id = @locationId
                ORDER BY l.is_active, l.name
                """,
                new { locationId = request.LocationId },
                cancellationToken: cancellationToken));

        return locationDto.FirstOrDefault();
    }
}