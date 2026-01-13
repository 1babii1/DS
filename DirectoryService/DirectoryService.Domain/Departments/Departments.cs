using CSharpFunctionalExtensions;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;
using Shared;

namespace DirectoryService.Domain.Departments;

public sealed class Departments : ISoftDeletable
{
    public DepartmentId Id { get; private set; } = null!;

    public DepartmentName Name { get; private set; } = null!;

    public DepartmentIdentifier Identifier { get; private set; } = null!;

    public DepartmentPath Path { get; private set; } = null!;

    public DepartmentId? ParentId { get; private set; }

    public short Depth { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public DateTime? DeletedAt { get; set; }

    public IReadOnlyList<Departments> DepartmentsChildrenList { get; private set; } = null!;

    public IReadOnlyList<DepartmentPosition> DepartmentsPositionsList { get; private set; } = null!;

    public IReadOnlyList<DepartmentLocation> DepartmentsLocationsList { get; private set; } = null!;

    // EF Core
    public Departments() { }

    private Departments(DepartmentId id, DepartmentName name, DepartmentIdentifier identifier, DepartmentPath path,
        short depth, DepartmentId? parentId)
    {
        Id = id;
        Name = name;
        Identifier = identifier;
        Path = path;
        ParentId = parentId;
        Depth = depth;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static Result<Departments, Error> CreateParent(DepartmentName name, DepartmentIdentifier identifier,
        IEnumerable<LocationId> locationIds, DepartmentId? departmentId = null)
    {
        IEnumerable<LocationId> departmentLocationsList = locationIds.ToList();
        if (!departmentLocationsList.Any())
        {
            return Error.Validation("department.location", "To create a department, you must have a location");
        }

        var departmentPathResult = DepartmentPath.CreateParent(identifier.Value);

        if (departmentPathResult.IsFailure)
        {
            return Error.Validation("department.path", "Invalid department path");
        }

        DepartmentPath departmentPath = departmentPathResult.Value;

        Departments departments = new(departmentId ?? DepartmentId.NewDepartmentId(), name, identifier, departmentPath,
            0, null);

        return Result.Success<Departments, Error>(departments);
    }

    public static Result<Departments, Error> CreateChild(
        DepartmentName name,
        DepartmentIdentifier identifier,
        Departments department,
        IEnumerable<LocationId> locationIds,
        DepartmentId? departmentId = null)
    {
        IEnumerable<LocationId> departmentLocationsList = locationIds.ToList();
        if (!departmentLocationsList.Any())
        {
            return Error.Validation("department.location", "To create a department, you must have a location");
        }

        var pathResult = DepartmentPath.CreateChild(identifier.Value, department.Path);

        if (pathResult.IsFailure)
        {
            return Error.Validation("department.path", "Invalid department path");
        }

        DepartmentPath pathChild = pathResult.Value;

        DepartmentId parentId = DepartmentId.FromValue(department.Id.Value);

        Departments departments = new(departmentId ?? DepartmentId.NewDepartmentId(), name, identifier, pathChild,
            (short)(department.Depth + 1), parentId);

        return Result.Success<Departments, Error>(departments);
    }

    public void SetName(DepartmentName name) => Name = name;

    public void SetIdentifier(DepartmentIdentifier identifier) => Identifier = identifier;

    public void SetPath(DepartmentPath path) => Path = path;

    public void SetParentId(DepartmentId? parentId) => ParentId = parentId;

    public void SetDepth(short depth) => Depth = depth;

    public void SetCreatedAt(DateTime createdAt) => CreatedAt = createdAt;

    public void SetUpdatedAt(DateTime updatedAt) => UpdatedAt = updatedAt;

    public void SetDepartmentsPositionsList(IReadOnlyList<DepartmentPosition> departmentsPositionsList) =>
        DepartmentsPositionsList = departmentsPositionsList;

    public void SetDepartmentsLocationsList(IEnumerable<DepartmentLocation> departmentsLocationsList) =>
        DepartmentsLocationsList = departmentsLocationsList.ToList();

    public void Delete()
    {
        IsActive = false;
        Path = Path.ChangePath();
        DeletedAt = DateTime.UtcNow;
    }

    public void Activate() => IsActive = true;
}