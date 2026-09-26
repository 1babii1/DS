using CSharpFunctionalExtensions;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using Shared;

namespace DirectoryService.Application.Database;

public interface ILocationsRepository
{
    Task<Result<Guid, Error>> Add(Domain.Locations.Location locations, CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<LocationId>, Error>> GetLocationsIds(
        IEnumerable<LocationId> locationIds,
        CancellationToken cancellationToken);

    Task<Result<IEnumerable<Domain.Locations.Location>, Error>> GetOrphanLocationByDepartment(
        DepartmentId departmentId,
        CancellationToken cancellationToken);
}