using CSharpFunctionalExtensions;
using DirectoryService.Application.Database;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Repositories.Positions;

public class EfCorePositionRepository : IPositionRepository
{
    private readonly DirectoryServiceDbContext _dbContext;
    private readonly ILogger<EfCorePositionRepository> _logger;

    public EfCorePositionRepository(DirectoryServiceDbContext dbContext, ILogger<EfCorePositionRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<Guid, Error>> Add(
        Domain.Positions.Position position,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _dbContext.Positions.AddAsync(position, cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return position.Id.Value;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error adding position");

            return Error.Failure("position.insert", "Fail to insert position");
        }
    }

    public async Task<Result<IEnumerable<Position>, Error>> GetOrphanPositionByDepartment(
        DepartmentId departmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var locationsDepartment = await _dbContext.DepartmentPositions
                .Where(dp => dp.DepartmentId == departmentId)
                .GroupBy(dp => dp.PositionId)
                .Where(g => g.Count() == 1)
                .Select(g => g.Key)
                .ToListAsync(cancellationToken);

            if (locationsDepartment.Any())
            {
                var locations = await _dbContext.Positions
                    .Where(p => locationsDepartment.Contains(p.Id) && p.IsActive == true)
                    .ToListAsync(cancellationToken);
                return Result.Success<IEnumerable<Position>, Error>(locations);
            }

            return Result.Success<IEnumerable<Position>, Error>(new List<Position>());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting position");

            return Error.Failure("position.get", "Fail to get position");
        }
    }
}