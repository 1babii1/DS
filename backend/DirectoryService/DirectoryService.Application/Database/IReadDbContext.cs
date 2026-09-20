using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Locations;

namespace DirectoryService.Application.Database;

public interface IReadDbContext
{
    IQueryable<Domain.Departments.Department> DepartmentsRead { get; }

    IQueryable<Domain.Locations.Location> LocationsRead { get; }

    IQueryable<Domain.Positions.Position> PositionsRead { get; }

    IQueryable<DepartmentLocation> DepartmentsLocationsRead { get; }

    IQueryable<DepartmentPosition> DepartmentsPositionsRead { get; }
}