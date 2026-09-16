namespace DirectoryService.Application.IntegrationEvents;

public static class PositionEventTypes
{
    public const string Created = "PositionCreated";
}

public record PositionCreatedEvent(Guid PositionId, string Name, string? Description, Guid[] DepartmentIds);
