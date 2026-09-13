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

    Task<UnitResult<Error>> LockChildrenByPath(
        DepartmentPath path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-parents a department and rewrites the paths and depths of its whole subtree.
    /// Depth is derived from the new path, so no depth argument is needed.
    /// </summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    Task<UnitResult<Error>> UpdateHierarchy(
        DepartmentId newParentId,
        DepartmentPath newParentPath,
        DepartmentId currentId,
        DepartmentPath oldPath,
        CancellationToken cancellationToken = default);

    Task<Result<Guid, Error>> Add(Departments department, CancellationToken cancellationToken = default);

    Task<Result<IEnumerable<DepartmentId>, Error>> GetDepartmentsIds(
        IEnumerable<DepartmentId> departmentIds,
        CancellationToken cancellationToken);
}