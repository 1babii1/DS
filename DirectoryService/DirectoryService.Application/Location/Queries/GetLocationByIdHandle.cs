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

    // public async Task<ReadLocationDto?> Handle(GetLocationByIdRequest request, CancellationToken cancellationToken)
    // {
    //     var location = await _readDbContext.LocationsRead
    //         .FirstOrDefaultAsync(l => l.Id == LocationId.FromValue(request.LocationId), cancellationToken);
    //
    //     if (location is null)
    //     {
    //         return null;
    //     }
    //
    //     return new ReadLocationDto
    //     {
    //         Id = location.Id.Value,
    //         Name = location.Name.Value,
    //         Timezone = location.Timezone.Value,
    //         Country = location.Address.Country,
    //         City = location.Address.City,
    //         Street = location.Address.Street,
    //         IsActive = location.IsActive,
    //         CreatedAt = location.CreatedAt,
    //         UpdatedAt = location.UpdatedAt,
    //     };
    // }
    public async Task<ReadLocationDto?> Handle(GetLocationByIdRequest request, CancellationToken cancellationToken)
    {
        var connection = await _dbConnectionFactory.CreateConnectionAsync(cancellationToken);

        var locationDto = await connection.QueryAsync<ReadLocationDto>(
            """
            SELECT l.id, l.name, l.timezone, l.street, l.city, l.country, l.is_active, l.created_at, l.updated_at FROM locations l
            WHERE l.id = @locationId
            ORDER BY l.is_active, l.name
            """,
            param: new { locationId = request.LocationId });

        return locationDto.FirstOrDefault();
    }
}