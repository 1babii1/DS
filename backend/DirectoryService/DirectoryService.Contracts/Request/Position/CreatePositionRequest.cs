using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions.ValueObjects;

namespace DirectoryService.Contracts.Request.Position;

public record CreatePositionRequest(PositionName Name, PositionDescription? Description, IEnumerable<DepartmentId> DepartmentIds);