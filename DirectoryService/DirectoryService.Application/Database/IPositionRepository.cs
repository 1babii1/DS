using CSharpFunctionalExtensions;
using DirectoryService.Domain.Departments.ValueObjects;
using Shared;

namespace DirectoryService.Application.Database;

public interface IPositionRepository
{
    Task<Result<Guid, Error>> Add(Domain.Positions.Position position, CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<Domain.Positions.Position>, Error>> GetOrphanPositionByDepartment(
        DepartmentId departmentId,
        CancellationToken cancellationToken);
}