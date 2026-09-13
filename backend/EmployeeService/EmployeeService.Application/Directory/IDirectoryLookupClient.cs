namespace EmployeeService.Application.Directory;

public record DepartmentLookupResult(bool Found, string Name, bool IsActive);

public record PositionLookupResult(bool Found, string Name, bool IsActive);

public record AssignmentValidationResult(
    bool DepartmentExists,
    bool DepartmentActive,
    string DepartmentName,
    bool PositionExists,
    bool PositionActive,
    string PositionName,
    bool PositionBelongsToDepartment)
{
    public bool IsValid =>
        DepartmentExists && DepartmentActive && PositionExists && PositionActive && PositionBelongsToDepartment;
}

public interface IDirectoryLookupClient
{
    Task<DepartmentLookupResult> GetDepartmentAsync(Guid departmentId, CancellationToken cancellationToken);

    Task<PositionLookupResult> GetPositionAsync(Guid positionId, CancellationToken cancellationToken);

    Task<AssignmentValidationResult> ValidateAssignmentAsync(
        Guid departmentId,
        Guid positionId,
        CancellationToken cancellationToken);
}