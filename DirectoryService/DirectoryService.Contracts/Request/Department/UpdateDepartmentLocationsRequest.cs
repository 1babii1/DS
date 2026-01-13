using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;

namespace DirectoryService.Contracts.Request.Department;

public record UpdateDepartmentLocationsRequest(DepartmentId departmentId, IEnumerable<LocationId> locationIds);