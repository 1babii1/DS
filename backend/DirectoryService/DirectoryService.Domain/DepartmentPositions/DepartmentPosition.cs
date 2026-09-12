using CSharpFunctionalExtensions;
using DirectoryService.Domain.DepartmentPositions.ValueObjects;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions.ValueObjects;

namespace DirectoryService.Domain.DepartmentPositions;

public class DepartmentPosition
{
    public DepartmentPositionsId Id { get; private set; }

    public DepartmentId DepartmentId { get; private set; }

    public PositionId PositionId { get; private set; }

    private DepartmentPosition(DepartmentPositionsId id, DepartmentId departmentId, PositionId positionId)
    {
        Id = id;
        DepartmentId = departmentId;
        PositionId = positionId;
    }

    public static Result<DepartmentPosition> Create(DepartmentPositionsId? id, DepartmentId departmentId,
        PositionId positionId)
    {
        DepartmentPosition departmentPosition = new(id ?? DepartmentPositionsId.NewDepartmentPositionsId(), departmentId, positionId);

        return Result.Success(departmentPosition);
    }

    public void SetId(DepartmentPositionsId id) => Id = id;

    public void SetDepartmentId(DepartmentId departmentId) => DepartmentId = departmentId;

    public void SetPositionId(PositionId positionId) => PositionId = positionId;
}