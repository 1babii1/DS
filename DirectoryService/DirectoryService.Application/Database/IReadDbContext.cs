using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Locations;

namespace DirectoryService.Application.Database;

public interface IReadDbContext
{
    IQueryable<Departments> DepartmentsRead { get; }

    IQueryable<Locations> LocationsRead { get; }

    IQueryable<Domain.Positions.Position> PositionsRead { get; }

    IQueryable<DepartmentLocation> DepartmentsLocationsRead { get; }

    IQueryable<DepartmentPosition> DepartmentsPositionsRead { get; }
}