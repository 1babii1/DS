namespace EmployeeService.Web.Consumers;

// What this service reads of DirectoryService's department events (ADR 0057): the parts that say where a department sits in the tree. A local copy
// of the shape, as with the other consumers here; the reader schemas beside it (Schemas/*.avsc) are checked against the producer's in CI.
public record DepartmentCreatedEvent(Guid DepartmentId, Guid? ParentDepartmentId)
{
    public const string MessageType = "DepartmentCreated";
}

public record DepartmentMovedEvent(Guid DepartmentId, Guid NewParentDepartmentId)
{
    public const string MessageType = "DepartmentMoved";
}

public record DepartmentDeletedEvent(Guid DepartmentId)
{
    public const string MessageType = "DepartmentDeleted";
}
