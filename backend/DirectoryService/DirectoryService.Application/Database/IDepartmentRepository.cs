using CSharpFunctionalExtensions;
using DirectoryService.Contracts.Response.Department;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;
using Shared;

namespace DirectoryService.Application.Database;

public interface IDepartmentRepository
{
    Task Save();

    Task<Result<Departments, Error>> GetByIdIncludeLocations(
        DepartmentId departmentIdId,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<Departments>, Error>> GetById(
        IReadOnlyList<DepartmentId> departmentIds,
        CancellationToken cancellationToken);

    Task<Result<Domain.Departments.Departments, Error>> GetById(
        DepartmentId departmentId,
        CancellationToken cancellationToken);

    Task<Result<Domain.Departments.Departments, Error>> GetByIdWithLock(
        DepartmentId departmentIdId,
        CancellationToken cancellationToken);

    Task<List<DepartmentDto>> GetHierarchy(
        DepartmentPath newDepartmentPath,
        CancellationToken cancellationToken = default);

    Task<UnitResult<Error>> LockChildrenByPath(
        DepartmentPath path,
        CancellationToken cancellationToken = default);

    Task<UnitResult<Error>> UpdateHierarchy(
        DepartmentId newParentId,
        DepartmentPath newParentPath,
        DepartmentId currentId,
        DepartmentPath oldPath,
        short depth,
        CancellationToken cancellationToken = default);

    Task<Result<Guid, Error>> Add(Departments department, CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<DepartmentId>, Error>> GetDepartmentsIds(
        IEnumerable<DepartmentId> departmentIds,
        CancellationToken cancellationToken);
}