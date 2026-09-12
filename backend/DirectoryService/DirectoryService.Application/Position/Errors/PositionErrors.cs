using Shared;

namespace DirectoryService.Application.Position.Errors;

public class PositionErrors
{
    public static Error DepartmentIdsNotFound() => Error.Validation("position.validation", "Department ids not found");
}