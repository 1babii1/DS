namespace DirectoryService.Application.IntegrationEvents;

public static class DepartmentEventTypes
{
    public const string Created = "DepartmentCreated";
    public const string Deleted = "DepartmentDeleted";
    public const string Moved = "DepartmentMoved";
}

public record DepartmentCreatedEvent(Guid DepartmentId, string Name, string Identifier, Guid? ParentDepartmentId);

public record DepartmentDeletedEvent(Guid DepartmentId);

public record DepartmentMovedEvent(Guid DepartmentId, Guid? OldParentDepartmentId, Guid NewParentDepartmentId);