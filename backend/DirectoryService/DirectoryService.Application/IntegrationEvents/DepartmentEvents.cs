namespace DirectoryService.Application.IntegrationEvents;

public static class DepartmentEventTypes
{
    public const string Created = "DepartmentCreated";
    public const string Deleted = "DepartmentDeleted";
}

public record DepartmentCreatedEvent(Guid DepartmentId, string Name, string Identifier, Guid? ParentDepartmentId);

public record DepartmentDeletedEvent(Guid DepartmentId);