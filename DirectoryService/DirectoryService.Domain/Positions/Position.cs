using CSharpFunctionalExtensions;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Positions.ValueObjects;
using Shared;

namespace DirectoryService.Domain.Positions;

public class Position : ISoftDeletable
{
    public PositionId Id { get; private set; } = null!;

    public PositionName Name { get; private set; } = null!;

    public PositionDescription? Description { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public DateTime? DeletedAt { get; set; }

    public IReadOnlyList<DepartmentPosition> DepartmentPositionsList { get; private set; } = null!;

    // EF Core
    public Position() { }

    public Position(PositionId id, PositionName name, IReadOnlyList<DepartmentPosition> departmentPositionsList,
        PositionDescription? description = null)
    {
        Id = id;
        Name = name;
        Description = description;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        DepartmentPositionsList = departmentPositionsList.ToList();
    }

    public void SetId(PositionId id) => Id = id;

    public void SetName(PositionName name) => Name = name;

    public void SetDescription(PositionDescription description) => Description = description;

    public void SetIsActive(bool isActive) => IsActive = isActive;

    public void SetCreatedAt(DateTime createdAt) => CreatedAt = createdAt;

    public void SetUpdatedAt(DateTime updatedAt) => UpdatedAt = updatedAt;

    public void SetDepartmentPositionsList(IReadOnlyList<DepartmentPosition> departmentPositionsList) =>
        DepartmentPositionsList = departmentPositionsList;

    public void Delete()
    {
        IsActive = false;
        DeletedAt = DateTime.UtcNow;
    }

    public void Activate() => IsActive = true;
}