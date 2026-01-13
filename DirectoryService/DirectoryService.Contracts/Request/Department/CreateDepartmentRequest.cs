using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;

namespace DirectoryService.Contracts.Request.Department;

public record CreateDepartmentRequest(
    DepartmentName Name,
    DepartmentIdentifier Identifier,
    DepartmentId? ParentDepartmentId,
    short? Depth,
    IEnumerable<LocationId> LocationsIds,
    DepartmentId? DepartmentId);