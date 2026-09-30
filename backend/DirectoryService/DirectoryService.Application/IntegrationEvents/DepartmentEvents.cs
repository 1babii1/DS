namespace DirectoryService.Application.IntegrationEvents;

public static class DepartmentEventTypes
{
    public const string Created = "DepartmentCreated";
    public const string Deleted = "DepartmentDeleted";
    public const string Moved = "DepartmentMoved";
    public const string Renamed = "DepartmentRenamed";
}

public record DepartmentCreatedEvent(Guid DepartmentId, string Name, string Identifier, Guid? ParentDepartmentId);

public record DepartmentDeletedEvent(Guid DepartmentId);

public record DepartmentMovedEvent(Guid DepartmentId, Guid? OldParentDepartmentId, Guid NewParentDepartmentId);

// Carries the identifier as well as the name: a consumer that rebuilds its document from this event has everything
// the department is indexed by, without a call back to DirectoryService.
public record DepartmentRenamedEvent(Guid DepartmentId, string Name, string Identifier);
