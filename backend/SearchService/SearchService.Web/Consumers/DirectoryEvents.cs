namespace SearchService.Web.Consumers;

// Deliberately local copies of DirectoryService's integration events - same reasoning as
// every other cross-service event copy in this codebase. Only the fields this consumer
// actually needs.
public record DepartmentCreatedEvent(Guid DepartmentId, string Name, string Identifier, Guid? ParentDepartmentId)
{
    public const string MessageType = "DepartmentCreated";
}

public record DepartmentDeletedEvent(Guid DepartmentId)
{
    public const string MessageType = "DepartmentDeleted";
}

public record PositionCreatedEvent(Guid PositionId, string Name, string? Description, Guid[] DepartmentIds)
{
    public const string MessageType = "PositionCreated";
}

public record LocationCreatedEvent(Guid LocationId, string Name, string Street, string City, string Country, string Timezone)
{
    public const string MessageType = "LocationCreated";
}
